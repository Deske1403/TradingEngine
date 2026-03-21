#nullable enable
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.MetricsServer;
using Denis.TradingEngine.Strategy.Filters;
using Denis.TradingEngine.Strategy.Pullback.Indicators;
using Denis.TradingEngine.Strategy.Pullback.TimeGate;
using Denis.TradingEngine.Strategy.Signals;
using Serilog;

namespace Denis.TradingEngine.Strategy.Pullback
{
    /// <summary>
    /// Pullback u uzlaznom trendu (bez baze, samo live feed).
    ///
    /// Ideja:
    ///  - uptrend: EMA_fast > EMA_slow i EMA_slow ne pada
    ///  - čekamo da cena ode ispod EMA_fast (pullback), ali ostane iznad EMA_slow
    ///  - skupljamo lokalni high/low tokom pullback-a
    ///  - ulazimo kad cena ponovo probije iznad EMA_fast i uzme lokalni pullback high (reclaim)
    ///
    /// Filtri:
    ///  - spread <= MaxSpreadBps
    ///  - min aktivnost (MinTicksPerWindow u ActivityWindowSeconds)
    ///  - ATR warmup
    ///  - min vreme između signala po simbolu
    /// </summary>
    public sealed class PullbackInUptrendStrategy : ITradingStrategy
    {
        public event Action<TradeSignal>? TradeSignalGenerated;
        private readonly string _strategyName = nameof(PullbackInUptrendStrategy);
        private readonly ILogger _log = Log.ForContext<PullbackInUptrendStrategy>();
        private readonly PullbackConfigRoot _cfgRoot;

        // NEW: Slayer preko konstrukora
        private readonly SignalSlayer _slayer;
        private readonly IActivityTicksProvider? _activityTicksProvider;

        // === Runtime parametri koji važe za "trenutni" simbol u OnQuote ===
        // ATR
        public int AtrPeriod { get; private set; } = 14;
        public decimal MinAtrFractionOfPrice { get; private set; } = 0.000000001m;
        // EMA trend
        public int EmaFastPeriod { get; private set; } = 20;
        public int EmaSlowPeriod { get; private set; } = 50;
        // Aktivnost (proxy za volume)
        public int ActivityWindowSeconds { get; private set; } = 60;
        public int MinTicksPerWindow { get; private set; } = 2;
        // Likvidnost
        public decimal MaxSpreadBps { get; private set; } = 25m;
        // Pullback geometrija
        public decimal MinPullbackBelowFastPct { get; private set; } = 0.00015m;
        public decimal MaxBelowSlowPct { get; private set; } = 0.02m;
        /// <summary>Min (pbHi - pbLo) / price; ispod = jitter, ne emituj. Default 0.01%.</summary>
        public decimal MinPullbackDepthPct { get; private set; } = 0.0001m;
        // Trajanje pullback-a
        public TimeSpan MinPullbackDuration { get; private set; } = TimeSpan.FromSeconds(3);
        public TimeSpan MaxPullbackDuration { get; private set; } = TimeSpan.FromMinutes(5);
        // Razmak između signala po simbolu
        public TimeSpan MinTimeBetweenSignals { get; private set; } = TimeSpan.FromSeconds(30);
        // Breakout buffer
        public decimal BreakoutBufferPct { get; private set; } = 0.0005m;
        // Hysteresis za trend broken abort (koliko tick-ova da čeka pre abort-a)
        // 15 tickova ~= 0.5s na aktivnim simbolima; 3 je bilo preosetljivo za tick-level data
        public int TrendBrokenHysteresisTicks { get; private set; } = 15;
        // Trading config (IBKR only)
        private readonly bool _useMidPrice;
        private readonly int _minQuantity;
        private bool _debugLogging;
        private sealed class SymbolState
        {
            public int TickCount;

            public decimal? Last;
            public decimal? Bid;
            public decimal? Ask;

            // ATR proxy
            public decimal? PrevLast;
            public decimal? Atr;
            public readonly Queue<decimal> TrWindow = new();

            // EMA fast/slow
            public decimal? EmaFast;
            public decimal? EmaSlow;
            public decimal? PrevEmaSlow;

            // Aktivnost
            public readonly Queue<DateTime> TickTimes = new();

            // Pullback state
            public bool InPullback;
            public decimal PullbackHigh;
            public decimal PullbackLow;
            public DateTime PullbackStartUtc;
            // High from the most recent "above EMA fast" leg (anchor for next pullback breakout)
            public decimal? LastAboveFastHigh;
            public bool WasAboveFastPrev;
            
            // Frozen snapshot at PB start (ne menja se tokom PB-a)
            public string? RegimeAtStart;
            public decimal? Slope5AtStart;
            public decimal? Slope20AtStart;
            public decimal? AtrAtStart;
            public decimal? AtrFractionAtStart;
            
            // Edge-case detection
            public decimal? PrevPrice; // za gap detection
            public bool WasAborted; // za statistiku
            public string? AbortReason; // za statistiku
            
            // Hysteresis za trend broken abort (ne abortuje odmah, čeka nekoliko tick-ova)
            public int TrendBrokenTickCount; // broj tick-ova gde je trend broken

            // Throttling
            public DateTime? LastSignalUtc;
            public bool ConfigLogged;

            // Analytics add-ons (per-symbol)
            public readonly TrendSlope Slope5 = new(5);
            public readonly TrendSlope Slope20 = new(20);
            public readonly MovingAverage Ma10 = new(10);
            public readonly MovingAverage Ma30 = new(30);

        }

        private readonly Dictionary<string, SymbolState> _state = new(StringComparer.OrdinalIgnoreCase);

        public PullbackInUptrendStrategy(
            PullbackConfigRoot cfgRoot, 
            SignalSlayerConfig slayerConfig,
            SignalSlayerDecisionRepository? slayerRepo = null,
            string? runEnv = null,
            bool useMidPrice = false,
            int minQuantity = 3,
            IActivityTicksProvider? activityTicksProvider = null)
        {
            _cfgRoot = cfgRoot ?? throw new ArgumentNullException(nameof(cfgRoot));
            _useMidPrice = useMidPrice;
            _minQuantity = minQuantity;
            _activityTicksProvider = activityTicksProvider;
            
            // Per-signal micro-filter config provider (exchange-aware)
            Func<SignalSlayerContext, MicroSignalFilterConfig>? microFilterProvider = null;
            if (slayerConfig?.EnableMicroFilter == true)
            {
                microFilterProvider = ctx =>
                {
                    var resolved = _cfgRoot.Resolve(ctx.Exchange ?? string.Empty, ctx.Symbol);
                    var phase = TradingPhase.GetPhase(ctx.UtcNow);
                    var maxSpreadBps = resolved.MicroFilterMaxSpreadBps;
                    var minTicksPerWindow = resolved.MicroFilterMinTicksPerWindow;

                    if (phase == TradingPhase.Phase.Midday)
                    {
                        if (resolved.MicroFilterMiddayMaxSpreadBps.HasValue)
                            maxSpreadBps = Math.Min(maxSpreadBps, resolved.MicroFilterMiddayMaxSpreadBps.Value);

                        if (resolved.MicroFilterMiddayMinTicksPerWindow.HasValue)
                            minTicksPerWindow = Math.Max(minTicksPerWindow, resolved.MicroFilterMiddayMinTicksPerWindow.Value);
                    }

                    return new MicroSignalFilterConfig
                    {
                        Enabled = resolved.MicroFilterEnabled,
                        MinSlope5Bps = resolved.MicroFilterMinSlope5Bps,
                        MinSlope20Bps = resolved.MicroFilterMinSlope20Bps,
                        MinAtrFractionOfPrice = resolved.MicroFilterMinAtrFractionOfPrice,
                        MaxSpreadBps = maxSpreadBps,
                        MinTicksPerWindow = minTicksPerWindow
                    };
                };
            }

            _slayer = new SignalSlayer(
                slayerConfig ?? new SignalSlayerConfig(),
                dbRepo: slayerRepo,
                runEnv: runEnv,
                microFilterContextConfigProvider: microFilterProvider);
        }

        public void OnQuote(MarketQuote q)
        {
            if (q is null || q.Symbol is null)
                return;

            var quoteTs = q.TimestampUtc == default ? DateTime.UtcNow : q.TimestampUtc;

            //// WEEKEND GAP guard
            //if (!TradingSessionGuard.IsWeekendGapClosed(quoteTs))
            //{
            //    _log.Warning("[STR-GAP-BLOCK] weekend gap is still closed, no trading ts={NowUtc:o}", quoteTs);
            //    return;
            //}

            var key = q.Symbol.Ticker;
            var exchange = q.Symbol.Exchange ?? string.Empty;

            // ----------------- Per-symbol config (exchange-aware) -----------------
            var resolved = _cfgRoot.Resolve(exchange, key);
            if (!resolved.Enabled)
                return;

            ApplyConfig(resolved);
            _debugLogging = resolved.DebugLogging;

            var st = _state.TryGetValue(key, out var s)
                ? s
                : (_state[key] = new SymbolState());

            if (!st.ConfigLogged)
            {
                _log.Information(
                    "[STR-CFG] {Sym} [{Exch}] enabled={En} debug={Dbg} ema={EmaF}/{EmaS} act={ActWin}s/{ActMin} spreadMax={Spread}",
                    key, exchange, resolved.Enabled, resolved.DebugLogging,
                    resolved.EmaFastPeriod, resolved.EmaSlowPeriod,
                    resolved.ActivityWindowSeconds, resolved.MinTicksPerWindow,
                    resolved.MaxSpreadBps);
                st.ConfigLogged = true;
            }

            st.TickCount++;

            // ----------------- Snapshot prices -----------------
            st.Last = q.Last ?? st.Last;
            st.Bid = q.Bid ?? st.Bid;
            st.Ask = q.Ask ?? st.Ask;

            decimal px;
            if (st.Bid.HasValue && st.Ask.HasValue)
                px = (st.Bid.Value + st.Ask.Value) / 2m;
            else
                px = st.Last ?? 0m;

            // === Analytics mode ===
            st.Slope5.Add(px);
            st.Slope20.Add(px);
            st.Ma10.Add(px);
            st.Ma30.Add(px);

            if (_debugLogging)
            {
                _log.Information(
                    "[ANALYTICS] {Sym} [{Exch}] px={Px:F4} slope5={S5:F6} slope20={S20:F6} ma10={MA10:F4} ma30={MA30:F4}",
                    key, exchange, px,
                    st.Slope5.Slope ?? 0m,
                    st.Slope20.Slope ?? 0m,
                    st.Ma10.Value ?? 0m,
                    st.Ma30.Value ?? 0m
                );
            }

            if (px <= 0m)
                return;

            if (_debugLogging)
                _log.Information("[STR-DIAG] {Sym} [{Exch}] px={Px:F4} bid={Bid} ask={Ask} last={Last}", key, exchange, px, st.Bid, st.Ask, st.Last);

            // ----------------- Aktivnost -----------------
            var cutoff = quoteTs.AddSeconds(-ActivityWindowSeconds);
            int ticksInWindow;
            if (_activityTicksProvider != null)
            {
                ticksInWindow = _activityTicksProvider.GetTicksInWindow(exchange, key, cutoff);
            }
            else
            {
                st.TickTimes.Enqueue(quoteTs);
                while (st.TickTimes.Count > 0 && st.TickTimes.Peek() < cutoff)
                    st.TickTimes.Dequeue();
                ticksInWindow = st.TickTimes.Count;
            }

            if (_debugLogging)
                _log.Information("[STR-ACT] {Sym} [{Exch}] activity ticks={Ticks} windowSec={Win}", key, exchange, ticksInWindow, ActivityWindowSeconds);

            // ----------------- ATR proxy -----------------
            if (st.PrevLast is null)
            {
                st.PrevLast = px;
            }
            else
            {
                var tr = Math.Abs(px - st.PrevLast.Value);
                st.TrWindow.Enqueue(tr);
                if (st.TrWindow.Count > AtrPeriod)
                    st.TrWindow.Dequeue();

                st.PrevLast = px;
            }

            if (st.TrWindow.Count < AtrPeriod)
            {
                if (_debugLogging)
                    _log.Information("[STR-ATR] {Sym} [{Exch}] ATR not ready yet (px={Px:F4} winCnt={Cnt}/{Per})", key, exchange, px, st.TrWindow.Count, AtrPeriod);

                return;
            }

            var atrNow = Average(st.TrWindow);
            st.Atr = atrNow;

            var atrFrac = px > 0m ? atrNow / px : 0m;

            if (_debugLogging)
                _log.Information("[STR-ATR-DIAG] {Sym} [{Exch}] atr={Atr:F6} atrFrac={Frac:E6} px={Px:F4}", key, exchange, atrNow, atrFrac, px);

            // Prometheus: ATR fraction (nikad ne sme da sruši strategiju)
            try { StrategyMetrics.Instance.ObserveAtrFraction(_strategyName, key, (double)atrFrac); } catch { }

            // === ATR ne blokira strategiju, samo utiče na režim/diagnostiku ===
            if (atrFrac <= 0m)
            {
                if (_debugLogging)
                    _log.Information("[STR-ATR-PATCH] {Sym} [{Exch}] atrFrac<=0 frac={Frac:E6}, continuing with LOW regime", key, exchange, atrFrac);
            }
            else if (atrFrac < MinAtrFractionOfPrice)
            {
                if (_debugLogging)
                    _log.Information("[STR-ATR] {Sym} [{Exch}] atrFrac below MinAtrFractionOfPrice frac={Frac:E6} min={Min:E6} — NOT blocking, only marking as low volatility",
                        key, exchange, atrFrac, MinAtrFractionOfPrice);
            }

            // ----------------- Dinamički režimi po atrFrac -----------------
            string regime;
            if (atrFrac > 0.0005m) regime = "HIGH";
            else if (atrFrac > 0.00015m) regime = "NORMAL";
            else regime = "LOW";

            switch (regime)
            {
                case "HIGH":
                    MinPullbackDuration = TimeSpan.FromSeconds(Math.Max(1, resolved.MinPullbackDurationSec - 1));
                    break;

                case "NORMAL":
                    MinPullbackDuration = TimeSpan.FromSeconds(resolved.MinPullbackDurationSec);
                    break;

                case "LOW":
                    MinPullbackDuration = TimeSpan.FromSeconds(resolved.MinPullbackDurationSec + 1);
                    break;
            }

            if (_debugLogging)
            {
                _log.Information(
                    "[STR-REGIME] {Sym} [{Exch}] regime={Reg} atrFrac={Frac:E6} minPbBelowFast={Pb:F6} minPbDur={Dur:F1}s emaFPer={Ef} emaSPer={Es}",
                    key, exchange, regime, atrFrac, MinPullbackBelowFastPct, MinPullbackDuration.TotalSeconds, EmaFastPeriod, EmaSlowPeriod
                );
            }

            var atr = atrNow;

            // ----------------- EMA update -----------------
            UpdateEma(px, EmaFastPeriod, ref st.EmaFast);
            UpdateEma(px, EmaSlowPeriod, ref st.EmaSlow);

            if (st.EmaSlow.HasValue)
                st.PrevEmaSlow ??= st.EmaSlow;

            // ----------------- Likvidnost (spread) -----------------
            var spreadFrac = SignalHelpers.SpreadFraction(q);
            if (!spreadFrac.HasValue || spreadFrac.Value <= 0m)
            {
                if (_debugLogging)
                    _log.Information("[STR-LIQ] {Sym} [{Exch}] no valid bid/ask for spread calc", key, exchange);
                return;
            }

            var spreadBp = spreadFrac.Value * 10000m;
            if (spreadBp > MaxSpreadBps)
            {
                if (_debugLogging)
                    _log.Information("[STR-LIQ] {Sym} [{Exch}] spread too wide bp={Bp:F1} max={MaxBp:F1}", key, exchange, spreadBp, MaxSpreadBps);
                return;
            }

            // ----------------- Aktivnost filter -----------------
            if (ticksInWindow < MinTicksPerWindow)
            {
                if (_debugLogging)
                    _log.Information("[STR-ACT] {Sym} [{Exch}] low activity ticks={Ticks} min={Min}", key, exchange, ticksInWindow, MinTicksPerWindow);

                return;
            }

            // ----------------- Trend filter (uptrend) -----------------
            if (!st.EmaFast.HasValue || !st.EmaSlow.HasValue)
            {
                if (_debugLogging)
                    _log.Information("[STR-TREND] {Sym} [{Exch}] EMA not ready yet", key, exchange);

                return;
            }

            var emaF = st.EmaFast.Value;
            var emaS = st.EmaSlow.Value;
            var prevSlow = st.PrevEmaSlow ?? emaS;

            var uptrend = (emaF > emaS) && (emaS >= prevSlow);
            var isAboveFastNow = px > emaF;
            st.PrevEmaSlow = emaS;

            if (_debugLogging)
                _log.Information("[STR-TREND-DIAG] {Sym} [{Exch}] emaF={F:F2} emaS={S:F2} prevSlow={PS:F2} uptrend={Up}", key, exchange, emaF, emaS, prevSlow, uptrend);

            if (!uptrend)
            {
                if (st.InPullback)
                {
                    // HYSTERESIS: Ne abortujemo odmah, čekamo nekoliko tick-ova da potvrdimo da je trend stvarno pukao
                    st.TrendBrokenTickCount++;
                    
                    if (st.TrendBrokenTickCount >= TrendBrokenHysteresisTicks)
                    {
                        // EDGE-CASE: Double PB (PB reset dok je u toku) - trend pukao (potvrđeno nakon hysteresis)
                        st.WasAborted = true;
                        st.AbortReason = $"trend_broken:confirmed_after_{st.TrendBrokenTickCount}_ticks";
                        
                        _log.Information(
                            "[STR-PB-ABORT] {Sym} trend broken, reset pullback (double PB scenario) reason={Reason} ticks={Ticks}",
                            key, st.AbortReason, st.TrendBrokenTickCount
                        );

                        try { StrategyMetrics.Instance.PullbackAborted(_strategyName, key, "trend_broken"); } catch { }
                        st.InPullback = false;
                        st.TrendBrokenTickCount = 0; // reset
                    }
                    else
                    {
                        // Trend broken, ali još čekamo da potvrdimo (hysteresis)
                        if (_debugLogging)
                        {
                            _log.Information(
                                "[STR-PB-HYST] {Sym} trend broken, waiting for confirmation tick={Tick}/{Max}",
                                key, st.TrendBrokenTickCount, TrendBrokenHysteresisTicks
                            );
                        }
                    }
                }
                st.WasAboveFastPrev = false;
                st.LastAboveFastHigh = null;
                return;
            }
            
            // Trend je validan - resetujemo hysteresis counter ako je pullback aktivan
            if (st.InPullback && st.TrendBrokenTickCount > 0)
            {
                if (_debugLogging)
                {
                    _log.Information(
                        "[STR-PB-HYST-RESET] {Sym} trend recovered, resetting hysteresis counter (was={Count})",
                        key, st.TrendBrokenTickCount
                    );
                }
                st.TrendBrokenTickCount = 0;
            }

            // Track local high while price is above EMA fast.
            // We freeze this anchor when pullback starts and use it as breakout reference.
            if (isAboveFastNow)
            {
                if (!st.WasAboveFastPrev)
                    st.LastAboveFastHigh = px; // new above-fast leg
                else
                    st.LastAboveFastHigh = st.LastAboveFastHigh.HasValue
                        ? Math.Max(st.LastAboveFastHigh.Value, px)
                        : px;
            }
            st.WasAboveFastPrev = isAboveFastNow;

            // ----------------- Pullback state machine -----------------
            if (!st.InPullback)
            {
                var belowFast = (emaF - px) / emaF;
                var belowSlow = (emaS - px) / emaS;

                if (_debugLogging)
                {
                    _log.Information(
                        "[STR-PB-CHECK] {Sym} belowFast={BF:F4} need>={NeedFast:F4} belowSlow={BS:F4} max={MaxBS:F4}",
                        key, belowFast, MinPullbackBelowFastPct, belowSlow, MaxBelowSlowPct
                    );
                }

                if (belowFast < MinPullbackBelowFastPct)
                    return;

                if (belowSlow > MaxBelowSlowPct)
                {
                    if (_debugLogging)
                    {
                        _log.Information(
                            "[STR-PB-BLOCK] {Sym} pullback too deep under slow EMA belowSlow={Bs:F4} max={Max:F4}",
                            key, belowSlow, MaxBelowSlowPct
                        );
                    }
                    return;
                }

                st.InPullback = true;
                st.PullbackStartUtc = quoteTs;
                st.PullbackHigh = st.LastAboveFastHigh ?? px;
                st.PullbackLow = px;
                
                // FREEZE STATE: zamrznuti snapshot od momenta kad PB počne
                st.RegimeAtStart = regime;
                st.Slope5AtStart = st.Slope5.Slope;
                st.Slope20AtStart = st.Slope20.Slope;
                st.AtrAtStart = atr;
                st.AtrFractionAtStart = atrFrac;
                st.PrevPrice = px;
                st.WasAborted = false;
                st.AbortReason = null;
                st.TrendBrokenTickCount = 0; // reset hysteresis counter na start

                _log.Information(
                    "[STR-PB-START] {Sym} start pullback px={Px:F2} emaF={Efa:F2} emaS={Esa:F2} belowFast={Bf:F4} belowSlow={Bs:F4} " +
                    "regime={Reg} slope5={S5:F6} slope20={S20:F6} atrFrac={AtrFrac:E6}",
                    key, px, emaF, emaS, belowFast, belowSlow,
                    regime, st.Slope5AtStart ?? 0m, st.Slope20AtStart ?? 0m, atrFrac
                );
                
                // Offline evaluacija: pullback detected
                try { StrategyMetrics.Instance.PullbackDetected(_strategyName, key); } catch { }
                
                return;
            }

            // In pullback: update hi/lo + duration + edge-case guards

            // EDGE-CASE: Gap tick detection (skok bez kontinuiteta)
            if (st.PrevPrice.HasValue && st.AtrAtStart.HasValue && st.AtrAtStart.Value > 0m)
            {
                var priceChange = Math.Abs(px - st.PrevPrice.Value);
                // 10× ATR ili min $0.05 — ATR na tick-level je previše mali, 3× je bilo preosetljivo
                var gapThreshold = Math.Max(0.05m, st.AtrAtStart.Value * 10m);
                
                if (priceChange > gapThreshold)
                {
                    _log.Warning(
                        "[STR-PB-GAP] {Sym} GAP detected: px={Px:F2} prev={Prev:F2} change={Chg:F4} threshold={Thr:F4} atr={Atr:F4} — aborting PB",
                        key, px, st.PrevPrice.Value, priceChange, gapThreshold, st.AtrAtStart.Value
                    );
                    
                    st.WasAborted = true;
                    st.AbortReason = $"gap_tick:change={priceChange:F4}";
                    st.InPullback = false;
                    st.TrendBrokenTickCount = 0; // reset hysteresis
                    
                    try { StrategyMetrics.Instance.PullbackAborted(_strategyName, key, "gap_tick"); } catch { }
                    return;
                }
            }
            st.PrevPrice = px;

            // EDGE-CASE: Thin liquidity (ticks ok ali NBBO šupalj)
            if (st.Bid.HasValue && st.Ask.HasValue)
            {
                var spread = st.Ask.Value - st.Bid.Value;
                var mid = (st.Bid.Value + st.Ask.Value) / 2m;
                var spreadBps = (spread / mid) * 10000m;
                
                // Ako spread postane preširok tokom PB-a (npr. 2x veći od početnog)
                if (st.AtrAtStart.HasValue && mid > 0m)
                {
                    var maxSpreadBps = MaxSpreadBps * 2m; // 2x threshold
                    if (spreadBps > maxSpreadBps)
                    {
                        _log.Warning(
                            "[STR-PB-THIN-LIQ] {Sym} thin liquidity detected: spread={Spd:F1}bps max={Max:F1}bps — aborting PB",
                            key, spreadBps, maxSpreadBps
                        );
                        
                        st.WasAborted = true;
                        st.AbortReason = $"thin_liquidity:spread={spreadBps:F1}bps";
                        st.InPullback = false;
                        st.TrendBrokenTickCount = 0; // reset hysteresis
                        
                        try { StrategyMetrics.Instance.PullbackAborted(_strategyName, key, "thin_liquidity"); } catch { }
                        return;
                    }
                }
            }

            // PullbackHigh is frozen at pullback start (pre-pullback above-fast anchor).

            // low uvek pratimo
            st.PullbackLow = Math.Min(st.PullbackLow, px);

            var pbDuration = quoteTs - st.PullbackStartUtc;

            if (_debugLogging)
            {
                _log.Information(
                    "[STR-PB-STATE] {Sym} hi={Hi:F2} lo={Lo:F2} dur={Dur:F1}s",
                    key, st.PullbackHigh, st.PullbackLow, pbDuration.TotalSeconds
                );
            }

            try
            {
                StrategyMetrics.Instance.ObservePullbackDuration(
                    _strategyName, key, pbDuration.TotalSeconds
                );
            }
            catch
            {
                // metrics ne sme da sruši strategiju
            }

            if (pbDuration < MinPullbackDuration) return;

            if (pbDuration > MaxPullbackDuration)
            {
                st.WasAborted = true;
                st.AbortReason = $"too_long:duration={pbDuration.TotalSeconds:F1}s";
                
                _log.Information(
                    "[STR-PB-ABORT] {Sym} pullback too long dur={Dur:F1}s max={Max:F1}s reason={Reason}",
                    key, pbDuration.TotalSeconds, MaxPullbackDuration.TotalSeconds, st.AbortReason
                );

                try { StrategyMetrics.Instance.PullbackAborted(_strategyName, key, "too_long"); } catch { }
                st.InPullback = false;
                st.TrendBrokenTickCount = 0; // reset hysteresis
                return;
            }

            // reclaim fast
            if (px < emaF) return;

            _log.Information("[STR-PB-RECLAIM] {Sym} [{Exch}] px={Px:F2} emaF={F:F2}", key, exchange, px, emaF);

            // ----------------- Breakout check -----------------
            decimal effectiveBufferPct;
            var fixedRegime = st.RegimeAtStart ?? regime;

            switch (fixedRegime)
            {
                case "LOW":
                    effectiveBufferPct = BreakoutBufferPct * 0.1m;   // 0.5 bps
                    break;

                case "NORMAL":
                    effectiveBufferPct = BreakoutBufferPct * 0.5m;
                    break;

                case "HIGH":
                default:
                    effectiveBufferPct = BreakoutBufferPct;
                    break;
            }

            var breakoutLevel = st.PullbackHigh * (1m + effectiveBufferPct);

            // FIX: dok nema breakout-a -> čekaj (NE "sending BUY")
            if (px < breakoutLevel)
            {
                if (_debugLogging)
                {
                    _log.Information("[STR-SETUP-WAIT] {Sym} [{Exch}] px={Px:F2} < blvl={Lvl:F2} hi={Hi:F2} dur={Dur:F1}s regime={Reg} bufPct={Buf:E6}",
                        key, exchange, px, breakoutLevel, st.PullbackHigh, pbDuration.TotalSeconds, fixedRegime, effectiveBufferPct);
                }
                return;
            }

            // Tek sad je breakout validan
            _log.Information("[STR-SETUP-OK] {Sym} [{Exch}] breakout OK — emitting BUY px={Px:F2} blvl={Lvl:F2} hi={Hi:F2} dur={Dur:F1}s regime={Reg}",
                key, exchange, px, breakoutLevel, st.PullbackHigh, pbDuration.TotalSeconds, fixedRegime);

            try { StrategyMetrics.Instance.SetupFound(_strategyName, key); } catch { }

            // ----------------- Throttle: samo kad je setup stvarno OK -----------------
            if (st.LastSignalUtc is { } lastEmit &&
                (quoteTs - lastEmit) < MinTimeBetweenSignals)
            {
                _log.Information("[STR-THROTTLE-BLOCK] {Sym} [{Exch}] found setup, but dt={Dt:F1}s < {Min}s",
                    key, exchange, (quoteTs - lastEmit).TotalSeconds, MinTimeBetweenSignals.TotalSeconds);

                try { StrategyMetrics.Instance.SignalThrottled(_strategyName, key); } catch { }

                st.InPullback = false;
                st.TrendBrokenTickCount = 0; // reset hysteresis
                return;
            }

            // Limit cena za entry: UseMidPrice (config Trading.UseMidPrice) – MID = px (manji slippage na skupim akcijama), inače ASK.
            var suggestedLimit = _useMidPrice ? px : (q.Ask ?? px);

            // Min pullback depth gate – odbaci jitter (pbHi ≈ pbLo)
            var depthPct = px > 0m && st.PullbackHigh >= st.PullbackLow
                ? (st.PullbackHigh - st.PullbackLow) / px
                : 0m;
            if (depthPct < MinPullbackDepthPct)
            {
                _log.Information(
                    "[STR-PB-DEPTH] {Sym} [{Exch}] pullback too shallow depthPct={Depth:E6} min={Min:E6} pbHi={Hi:F2} pbLo={Lo:F2} regime={Reg}",
                    key, exchange, depthPct, MinPullbackDepthPct, st.PullbackHigh, st.PullbackLow, fixedRegime);
                try { StrategyMetrics.Instance.PullbackAborted(_strategyName, key, "pullback_depth_too_shallow"); } catch { }
                st.InPullback = false;
                st.TrendBrokenTickCount = 0;
                return;
            }

            var reason =
                $"pullback-reclaim px={px:F2} emaF={emaF:F2} emaS={emaS:F2} " +
                $"pbHi={st.PullbackHigh:F2} pbLo={st.PullbackLow:F2} " +
                $"dur={pbDuration.TotalSeconds:F1}s atr={atr:F4} spreadBp={spreadBp:F1} " +
                $"ticks={ticksInWindow} regime={fixedRegime} bufPct={effectiveBufferPct:E6}";

            // ----------------- Slayer hard filter -----------------
            // KORISTIMO ZAMRZNUTE VREDNOSTI (frozen snapshot) umesto live vrednosti
            
            // Calculate move percentage from pullback low (proxy for "from entry")
            decimal? movePctFromEntry = st.PullbackLow > 0m ? (px - st.PullbackLow) / st.PullbackLow : (decimal?)null;
            
            // Has valid pullback structure (we're in pullback and have high/low)
            var hasValidPullbackStructure = st.InPullback && st.PullbackHigh > 0m && st.PullbackLow > 0m;
            
            if (!PassesSignalSlayer(
                    symbol: key,
                    price: px,
                    atr: st.AtrAtStart ?? atr, // koristi zamrznuti ATR ako postoji
                    spreadBps: spreadBp,
                    activityTicks: ticksInWindow,
                    utcNow: quoteTs,
                    regime: fixedRegime,
                    slope5: st.Slope5.Slope, // live slope5 na reclaim/entry trenutku
                    slope20: st.Slope20.Slope, // live slope20 na reclaim/entry trenutku
                    exchange: q.Symbol.Exchange,
                    movePctFromEntry: movePctFromEntry,
                    hasValidPullbackStructure: hasValidPullbackStructure))
            {
                st.WasAborted = true;
                st.AbortReason = "signal_slayer_rejected";
                
                try { StrategyMetrics.Instance.PullbackAborted(_strategyName, key, "signal_slayer_rejected"); } catch { }
                st.InPullback = false;
                st.TrendBrokenTickCount = 0; // reset hysteresis
                return;
            }

            var sig = new TradeSignal(
                Symbol: q.Symbol,
                ShouldEnter: true,
                Side: OrderSide.Buy,
                SuggestedLimitPrice: suggestedLimit,
                Reason: reason,
                TimestampUtc: quoteTs,
                SpreadBps: spreadBp,
                ActivityTicks: ticksInWindow,
                Regime: fixedRegime,
                Slope5: st.Slope5.Slope,
                Slope20: st.Slope20.Slope
            );

            _log.Information("[STR] BUY {Sym} [{Exch}] @ {Px:F2} reason={Reason}", q.Symbol.Ticker, exchange, suggestedLimit, reason);

            // Offline evaluacija: valid breakout (signal emitted)
            try { StrategyMetrics.Instance.PullbackValidBreakout(_strategyName, key); } catch { }

            st.LastSignalUtc = quoteTs;
            st.InPullback = false;
            st.TrendBrokenTickCount = 0; // reset hysteresis

            TryEmit(sig);
        }

        private void ApplyConfig(PullbackRuntimeConfig cfg)
        {
            AtrPeriod = cfg.AtrPeriod;
            MinAtrFractionOfPrice = cfg.MinAtrFractionOfPrice;

            ActivityWindowSeconds = cfg.ActivityWindowSeconds;
            MinTicksPerWindow = cfg.MinTicksPerWindow;

            MaxSpreadBps = cfg.MaxSpreadBps;

            EmaFastPeriod = cfg.EmaFastPeriod;
            EmaSlowPeriod = cfg.EmaSlowPeriod;

            MinPullbackBelowFastPct = cfg.MinPullbackBelowFastPct;
            MaxBelowSlowPct = cfg.MaxBelowSlowPct;
            MinPullbackDepthPct = cfg.MinPullbackDepthPct;

            MinPullbackDuration = TimeSpan.FromSeconds(cfg.MinPullbackDurationSec);
            MaxPullbackDuration = TimeSpan.FromSeconds(cfg.MaxPullbackDurationSec);

            BreakoutBufferPct = cfg.BreakoutBufferPct;

            MinTimeBetweenSignals = TimeSpan.FromSeconds(cfg.MinTimeBetweenSignalsSec);
        }

        private void TryEmit(TradeSignal sig)
        {
            try
            {
                // metrics: signal emitted
                try
                {
                    // Legacy metrika (bez phase)
                    StrategyMetrics.Instance.SignalEmitted(
                        _strategyName,
                        sig.Symbol.Ticker,
                        sig.Side.ToString().ToUpperInvariant() // BUY/SELL
                    );
                    // Phase-aware metrika
                    StrategyMetrics.Instance.SignalGeneratedByPhase(
                        _strategyName,
                        sig.Symbol.Ticker,
                        sig.TimestampUtc
                    );
                    StrategyMetrics.Instance.SignalAcceptedByPhase(
                        _strategyName,
                        sig.Symbol.Ticker,
                        sig.TimestampUtc
                    );
                }
                catch
                {
                    // metrics nikad ne sme da sruši strategiju
                }

                Console.WriteLine("[STRATEGY] signal {0} side={1} price={2:F2} reason={3}",
                    sig.Symbol.Ticker,
                    sig.Side,
                    sig.SuggestedLimitPrice,
                    sig.Reason
                );
                TradeSignalGenerated?.Invoke(sig);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "TradeSignal handler failed");
            }
        }
        private static decimal Average(IEnumerable<decimal> values)
        {
            decimal sum = 0m;
            var count = 0;
            foreach (var v in values)
            {
                sum += v;
                count++;
            }

            return count == 0 ? 0m : sum / count;
        }
        private static void UpdateEma(decimal price, int period, ref decimal? ema)
        {
            // Ako nema kretanja u ceni — ostavi ema kakva jeste
            if (ema.HasValue && price == ema.Value)
                return;

            if (period <= 1)
            {
                ema = price;
                return;
            }

            var k = 2m / (period + 1m);

            if (!ema.HasValue)
            {
                // Prvi tick — samo postavi
                ema = price;
            }
            else
            {
                // Standardni EMA
                ema = ema.Value + k * (price - ema.Value);
            }
        }
        private bool PassesSignalSlayer(
         string symbol,
         decimal price,
         decimal atr,
         decimal? spreadBps,
         int activityTicks,
         DateTime utcNow,
         string regime,
         decimal? slope5,
         decimal? slope20,
         string? exchange = null,
         decimal? movePctFromEntry = null,
         bool? hasValidPullbackStructure = null)
        {
            var ctx = new SignalSlayerContext(
                Symbol: symbol,
                Price: price,
                Atr: atr,
                SpreadBps: spreadBps,
                ActivityTicks: activityTicks,
                UtcNow: utcNow,
                StrategyName: _strategyName,
                Regime: regime,

                // IMPORTANT: nullable, ne pretvaraj u 0
                Slope5: slope5,
                Slope20: slope20,

                AtrFractionOfPrice: (price > 0m ? atr / price : 0m),
                Exchange: exchange,
                MovePctFromEntry: movePctFromEntry,
                HasValidPullbackStructure: hasValidPullbackStructure
            );

            var decision = _slayer.ShouldAccept(ctx);

            if (!decision.Accepted)
            {
                _log.Information(
                    "[SLAYER-BLOCK] {Sym} BLOCKED — {Reason}",
                    symbol,
                    decision.ReasonText ?? "n/a"
                );
            }
            else if (_debugLogging)
            {
                _log.Information("[SLAYER-ACCEPT] {Sym} OK", symbol);
            }

            return decision.Accepted;
        }

    }
}
