#nullable enable
using Denis.TradingEngine.App.Config;
using Denis.TradingEngine.App.Trading.EodSkim;
using Denis.TradingEngine.Core.Accounts;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Core.Positions;
using Denis.TradingEngine.Core.Risk;
using Denis.TradingEngine.Core.Swing;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Logging;
using Denis.TradingEngine.Logging.Discord;
using Denis.TradingEngine.MetricsServer;
using Denis.TradingEngine.Orders;
using Denis.TradingEngine.Simulation;
using Denis.TradingEngine.Strategy.Filters;
using Denis.TradingEngine.Strategy.Pullback.TimeGate;
using Denis.TradingEngine.Strategy.Trend;
using Serilog;
using System.Collections.Concurrent;

namespace Denis.TradingEngine.App.Trading
{
    /// <summary>
    /// Glavni "lepak": feed -> strategija -> risk -> (paper/real) -> cash/positions/journal.
    /// Ovde živimo, ovde se sve spaja.
    /// </summary>
    public sealed class TradingOrchestrator : IDisposable
    {
        private readonly ILogger _log = AppLog.ForContext<TradingOrchestrator>();  
        // ---------------- CORE STANJE ----------------
        private readonly bool _isRealMode;
        private readonly PositionBook _positionBook = new(); // sve naše pozicije
        private readonly IMarketDataFeed _feed; // quote-ovi
        private readonly ITradingStrategy _strategy; // generiše TradeSignal
        private readonly IRiskValidator _risk; // kaze "moze / ne moze / koliko"
        private readonly CommissionSchedule _fees; // procena fee-ja
        private readonly IbkrEodSkimOptions _ibkrEodSkimOptions;
        private readonly IbkrEodSkimCoordinator _ibkrEodSkim;
        private readonly RiskLimits _limits; // max exposure / daily loss / 
        private readonly decimal _perSymbolBudgetUsd; // koliko hocemo po simbolu
        private readonly IDayGuardService? _dayGuards; // dnevni limiti
        private IOrderService? _orderService; // null => PAPER
        private readonly IExposureTracker? _exposure; // opcioni exposure cap
        private readonly IAccountCashService _cashService; // Free / Settling / Reserved
        private readonly PaperExecutionSimulator _paperSim = new(); // da se LIMIT popuni u paperu
        private readonly Action<MarketQuote>? _paperForwarder; // feed -> paperSim
        private readonly IOrderCoordinator _orders = new OrderCoordinator(); // pending store
        private readonly SwingTradingConfig _swingConfig;
        // zaštita od duplih exit-ova
        private readonly object _sync = new();
        private readonly Dictionary<string, DateTime> _lastExitUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _exitPending = new(StringComparer.OrdinalIgnoreCase);
        // TP/SL config (fixed)
        private readonly decimal _tpFraction = 0.014m;
        private readonly decimal _slFraction = 0.01m;
        // pozadinsko osvežavanje keša
        private readonly CancellationTokenSource _cashRefreshCts = new();
        private Core.Accounts.CashState _lastCash = new(0m, 0m, 0m);
        // spoljne (broker) pozicije
        private readonly IExternalPositionsProvider? _externalPositions;
        // broker capabilities (fractional / min qty)
        // RTH prozor (može null => isključeno)
        private readonly TimeSpan? _rthStartUtc;
        private readonly TimeSpan? _rthEndUtc;
        private readonly TimeSpan? _tradeStartOffsetFromOpenNy;
        private readonly TimeSpan? _tradeEndLocalNy;
        // journal fajl
        private readonly bool _useEstimatedCommissionOnReal = true;
        private readonly TradingSettings _settings;
        private readonly bool _tradingEnabled = true;
        // koliko čekamo brokera da primi order pre nego što odustanemo
        private readonly TimeSpan _brokerPlaceTimeout = TimeSpan.FromSeconds(5);
        private readonly TradeJournalRepository? _journalRepo;
        private readonly TradeFillRepository? _fillRepo;
        private readonly TradeSignalRepository? _signalRepo;
        private readonly BrokerOrderRepository? _orderRepo; // NOVO
        private readonly DailyPnlRepository? _pnlRepo;
        private readonly SwingPositionRepository? _swingPosRepo;
        private readonly DiscordNotifier? _discordNotifier;
        private readonly ITrendContextProvider? _trendContextProvider;
        // cache poslednjeg kvota po simbolu, za NBBO snapshot
        private readonly Dictionary<string, MarketQuote> _lastQuotes = new(StringComparer.OrdinalIgnoreCase);
        // equity floor zastita - da ne padnemo ispod npr. 500 USD
        private readonly decimal _equityFloorUsd;
        private readonly decimal _startEquityUsd;
        // koliko max toleriramo širinu spreada, npr. 0.5% = 0.005m
        private const decimal MaxSpreadFraction = 0.005m;
        // koliko stari quote jos vazi kao "svez"
        private static readonly TimeSpan MaxQuoteAgeRth      = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaxQuoteAgeExtended = TimeSpan.FromSeconds(30);
        private readonly Dictionary<string, AtrState> _atr = new();
        // ATR-based TP/SL multipliers
        private const decimal TpAtrMultiple = 2.5m;
        private const decimal SlAtrMultiple = 1.5m;
        // trailing preko ATR-a
        private const decimal TrailActivateAtrMultiple = 1.0m; // aktiviraj trailing nakon +1 ATR
        private const decimal TrailDistanceAtrMultiple = 0.7m; // stop ~0.7 ATR ispod max
        // trailing stop: koliko dozvolimo da se vrati od lokalnog maksimuma (1% dole)
        private readonly Dictionary<string, PositionRuntimeState> _posRuntime = new(StringComparer.OrdinalIgnoreCase);
        // posle gubitničkog trade-a, pauza pre novog ulaza na isti simbol
        private readonly TimeSpan _symbolCooldownAfterLoss = TimeSpan.FromMinutes(15);
        private readonly Dictionary<string, DateTime> _lastLossUtc = new(StringComparer.OrdinalIgnoreCase);
        // trailing stop
        private readonly decimal _trailActivateFraction = 0.01m; // aktivira se posle +1%
        private readonly decimal _trailDistanceFraction = 0.005m; // stop ~0.5% ispod najboljeg
        // max vreme držanja pozicije
        private readonly TimeSpan _maxHoldTime;
        // cumulative filled po orderu (koristi se samo za REAL fill-ove)
        private readonly Dictionary<string, decimal> _cumFilledByCorrId = new(StringComparer.Ordinal);
        // Discord user-facing notifikacije grupišemo po correlationId da partial fill ne spamuje kanal.
        private readonly Dictionary<string, AggregatedDiscordFillNotification> _discordFillByCorrId = new(StringComparer.Ordinal);
        private readonly decimal _minFreeCashUsd;
        // global strategy rate-limit (max X signala u Y sekundi)
        private readonly int _maxSignalsPerWindow = 5;
        private readonly TimeSpan _signalWindow = TimeSpan.FromSeconds(10);
        private readonly Queue<DateTime> _signalTimestamps = new();
        // ako je ATR veci od 5% cene, smatramo da je volatilnost nenormalna za entry
        // order rate limiting - minimalni razmak izmedju cancel poziva po broker orderu
        private readonly TimeSpan _minCancelSpacing = TimeSpan.FromSeconds(2);
        private readonly Dictionary<string, DateTime> _lastCancelUtcByBrokerId = new(StringComparer.Ordinal);
        private readonly TimeSpan _brokerCancelTimeout = TimeSpan.FromSeconds(3);
        // ATR sizing metadata - čuvamo za TradeJournalEntry
        private readonly Dictionary<string, (decimal RiskFraction, decimal? AtrUsed, decimal? PriceRisk)> _sizingMetadata = new(StringComparer.Ordinal);
        // Tick profiler - prati statistiku po simbolu i fazi
        private readonly TickProfiler _tickProfiler = new();



        // idempotentnost za real fills: pamti koje slice-ove smo vec obradili
        private readonly Dictionary<string, DateTime> _processedRealFills = new(StringComparer.Ordinal);
        private static readonly TimeSpan ProcessedFillTtl = TimeSpan.FromMinutes(30);
        // Exit orders collected during recovery for registration in IbkrOrderService
        private List<(int brokerOrderId, OrderRequest req, DateTime sentAtUtc)>? _exitOrdersForRegistration;
        // CorrelationId -> TWS orderId snapshot from reqOpenOrders (used to rebuild correct mapping after restart)
        private readonly Dictionary<string, int> _recoveredTwsByCorrelation = new(StringComparer.OrdinalIgnoreCase);

        // ================================
        //  CTOR
        // ================================
        public TradingOrchestrator(
            bool isRealMode,
            IMarketDataFeed feed,
            ITradingStrategy strategy,
            IRiskValidator risk,
            CommissionSchedule fees,
            RiskLimits limits,
            decimal perSymbolBudgetUsd,
            IOrderService? orderService = null,
            IDayGuardService? dayGuards = null,
            IExposureTracker? exposure = null,
            IAccountCashService? cashService = null,
            IExternalPositionsProvider? externalPositions = null,
            TradingSettings? settings = null,
            TradeJournalRepository? journalRepo = null,
            TradeFillRepository? fillRepo = null,
            TradeSignalRepository? signalRepo = null,
            BrokerOrderRepository? orderRepo = null,
            DailyPnlRepository? pnlRepo = null,
            SwingPositionRepository? swingPosRepo = null,
            decimal equityFloorUsd = 0m,
            decimal minFreeCashUsd = 0m,
            SwingTradingConfig? swingConfig = null,
            DiscordNotifier? discordNotifier = null,
            ITrendContextProvider? trendContextProvider = null,
            IbkrEodSkimOptions? ibkrEodSkimOptions = null
        )
        {

            _isRealMode = isRealMode;
            _log.Information("TradingOrchestrator created. Mode={Mode}", _isRealMode ? "REAL" : "PAPER");
            // equity floor setup
            _equityFloorUsd = equityFloorUsd;
            _startEquityUsd = cashService is null ? 0m : cashService.GetCashStateAsync().GetAwaiter().GetResult().Free;

            if (_equityFloorUsd > 0m)
            {
                _log.Information("[EQ-FLOOR] Enabled floor={Floor:F2} startApprox={Start:F2}", _equityFloorUsd, _startEquityUsd);
            }
            // min free cash setup
            _minFreeCashUsd = minFreeCashUsd;
            if (_minFreeCashUsd > 0m)
            {
                _log.Information("[CASH-FLOOR] Enabled min free cash={Floor:F2}", _minFreeCashUsd);
            }

            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _risk = risk ?? throw new ArgumentNullException(nameof(risk));
            _fees = fees ?? throw new ArgumentNullException(nameof(fees));
            _ibkrEodSkimOptions = ibkrEodSkimOptions ?? new IbkrEodSkimOptions();
            _ibkrEodSkim = new IbkrEodSkimCoordinator(_ibkrEodSkimOptions, _log.ForContext<IbkrEodSkimCoordinator>());
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
            _perSymbolBudgetUsd = perSymbolBudgetUsd;
            _dayGuards = dayGuards;
            _orderService = orderService; // može null (paper)
            _exposure = exposure;
            _cashService = cashService ?? throw new ArgumentNullException(nameof(cashService));
            _externalPositions = externalPositions;
            _journalRepo = journalRepo;
            _fillRepo = fillRepo;
            _signalRepo = signalRepo;
            _orderRepo = orderRepo;
            _pnlRepo = pnlRepo;
            _swingPosRepo = swingPosRepo;
            _discordNotifier = discordNotifier;
            _trendContextProvider = trendContextProvider;
            // 1) feed -> strategija
            _feed.MarketQuoteUpdated += _strategy.OnQuote;
            // 2) feed -> exit heuristika
            _feed.MarketQuoteUpdated += EvaluatePaperExitsOnQuote;
            // 3) feed -> paper simulator (da punimo paper LIMIT-e)
            _paperForwarder = q =>
            {
                try
                {
                    _paperSim.OnQuote(q.Symbol, q.Last, q.Bid, q.Ask, q);
                }
                catch
                { /* tolerantno */
                }
            };
            _feed.MarketQuoteUpdated += _paperForwarder;
            _feed.MarketQuoteUpdated += OnQuoteCached;
            _feed.MarketQuoteUpdated += UpdateAtrOnQuote;
            _feed.MarketQuoteUpdated += OnQuoteForTickProfiler;
            // 4) strategija -> orchestrator
            _strategy.TradeSignalGenerated += OnTradeSignal;
            // 5) paper simulator -> fill
            _paperSim.Filled += OnPaperFilled;
            // 6) pozadinski refresh keša (da ne zovemo servis u OnTradeSignal)
            _ = Task.Run(async () =>
            {
                var token = _cashRefreshCts.Token;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var cs = await _cashService.GetCashStateAsync().ConfigureAwait(false);
                        _lastCash = cs;
                    }
                    catch
                    { // možemo Warning ako treba
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    }
                    catch
                    {//ignore
                    }
                }
            });

            // 7) ako imamo real order service, slušaj ga (commission, cancel, partial)
            if (_orderService is not null)
            {
                _orderService.OrderUpdated += OnOrderUpdated;
            }

            var initialMode =
                _orderService is not null ? $"Real ({_orderService.GetType().Name})"
              : _isRealMode ? "Real (order service pending attach)"
              : "Paper (no order service)";

            _log.Information("TradingOrchestrator READY. InitialMode={Mode}", initialMode);
            _log.Information(
                "[EOD-SKIM] Coordinator initialized enabled={Enabled} dryRun={DryRun}",
                _ibkrEodSkimOptions.Enabled,
                _ibkrEodSkimOptions.DryRun);
            // 8) ako imamo eksterni izvor pozicija (npr. IBKR) - odmah ih pokupi
            if (externalPositions is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var list = await externalPositions.GetOpenPositionsAsync().ConfigureAwait(false);
                        foreach (var dto in list)
                        {
                            SyncExternalPosition(dto.Symbol, dto.Quantity, dto.AveragePrice);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[EXT-POS-SYNC] initial import failed");
                    }
                });
            }
            else
            {
                _log.Warning("[EXT-POS-SYNC] is null");
            }


            // 10) RTH prozor (ako je popunjeno u settings)
            _rthStartUtc = settings?.RthStartUtc;
            _rthEndUtc = settings?.RthEndUtc;
            _tradeStartOffsetFromOpenNy = settings?.TradeStartOffsetFromOpenNy;
            _tradeEndLocalNy = settings?.TradeEndLocalNy;
            _useEstimatedCommissionOnReal = settings?.UseEstimatedCommissionOnReal ?? true;
            _settings = settings ?? new TradingSettings();
            _tradingEnabled = settings?.Enabled ?? true;

            _swingConfig = swingConfig ?? new SwingTradingConfig();

            _log.Information(
                "[SWING-CONFIG] Mode={Mode} MaxHoldingDays={Days} CloseBeforeWeekend={Weekend} WeekendCutoffUtc={Cutoff}",
                _swingConfig.Mode,
                _swingConfig.MaxHoldingDays,
                _swingConfig.CloseBeforeWeekend,
                _swingConfig.WeekendCutoffUtc
            );

            _log.Information(
                "[PROTECT-TRADE-CONFIG] Enabled={Enabled} ArmProfitPct={ArmPct:P2} StopOffsetPct={StopOffset:P2}",
                _settings.ProtectTrade.Enabled,
                _settings.ProtectTrade.ArmProfitPct,
                _settings.ProtectTrade.StopOffsetPct);




            _maxHoldTime =
                _swingConfig.Mode == SwingMode.Swing && _swingConfig.MaxHoldingDays > 0
                    ? TimeSpan.FromDays(_swingConfig.MaxHoldingDays)
                    : TimeSpan.FromMinutes(30);

            // ctor end!
        }
        // =========================
        //  EXTERNAL POSITIONS (IBKR - lokalno)
        // =========================
        /// <summary>
        /// Jedan simbol iz brokera upiši u naš PositionBook.
        /// Zovemo je kad broker javlja "ja vec imam ovu poziciju".
        /// </summary>
        /// 


        private void SyncExternalPosition(string symbol, decimal quantity, decimal averagePrice)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                _log.Information("[EXT-POS-SYNC] symbol is empty");
                return;
            }

            // 1) PositionBook (naše lokalno stanje)
            var pos = _positionBook.GetOrCreate(symbol);
            pos.Override(quantity, averagePrice);

            var nowUtc = DateTime.UtcNow;

            // 2) Runtime state (za age/trailing/itd)
            lock (_sync)
            {
                if (quantity > 0m)
                {
                    // NOTE: na importu ne znamo pravi entryUtc; koristimo "sada".
                    // To je OK za monitoring, ali nema veze sa DB opened_utc (tamo čuvamo prvi put).
                    // For external positions, we don't have regime/baseline info, use defaults
                    _posRuntime[symbol] = new PositionRuntimeState
                    {
                        EntryUtc = nowUtc,
                        EntryPrice = averagePrice,
                        BestPrice = averagePrice,
                        IsExternal = true,
                        RegimeAtEntry = "NORMAL", // default for external
                        SymbolBaseline = "normal", // default for external
                        AtrAtEntry = null // unknown for external
                    };
                }
                else
                {
                    _posRuntime.Remove(symbol);
                }
            }

            _log.Debug("[EXT-POS-SYNC] {Sym} qty={Qty} avg={Avg}", symbol, quantity, averagePrice);

            // 3) SWING DB sync (IBKR import -> swing_positions)
            //    - radi samo u REAL + SWING
            if (_orderService is null)
                return;

            if (_swingPosRepo is null || !SwingHelpers.IsSwingMode(_swingConfig))
                return;

            try
            {
                if (quantity > 0m)
                {
                    var snap = new SwingPositionSnapshot
                    {
                        Symbol = symbol,
                        Quantity = quantity,
                        EntryPrice = averagePrice,
                        OpenedUtc = nowUtc, // koristi se samo ako nema open reda (repo čuva stari opened_utc ako je is_open)
                        Strategy = "External/IBKR",
                        CorrelationId = "extpos-import",
                        PlannedHoldingDays = _swingConfig.MaxHoldingDays,
                        ExitPolicy = SwingExitPolicy.PriceOrTime
                    };

                    var t = _swingPosRepo.UpsertOpenExternalAsync(snap, exchange: "SMART", CancellationToken.None);
                    _ = t.ContinueWith(
                        tt => _log.Error(tt.Exception, "[SWING-DB] UpsertOpenExternal faulted sym={Sym}", symbol),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
                else
                {
                    // Oprez: IBKR sync moze ponekad da vrati 0; ali u REAL+SWING to je vec redje.
                    var t = _swingPosRepo.MarkClosedAsync(
                         symbol: symbol,
                         exchange: "SMART",
                         closedUtc: nowUtc,
                         exitReason: SwingExitReason.ExternalSync,
                         autoExit: false,
                         ct: CancellationToken.None);

                    _ = t.ContinueWith(
                        tt => _log.Error(tt.Exception, "[SWING-DB] MarkClosed(import) faulted sym={Sym}", symbol),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[SWING-DB] external sync scheduling failed sym={Sym}", symbol);
            }
        }
        /// <summary>
        /// Batch verzija - vise komada odjednom.
        /// </summary>
        private void SyncExternalPositions(IEnumerable<(string symbol, decimal qty, decimal avg)> items)
        {
            foreach (var (symbol, qty, avg) in items)
            {
                SyncExternalPosition(symbol, qty, avg);
            }
        }
        // =========================
        //  SIGNAL (ENTRY)
        // =========================
        private void OnTradeSignal(TradeSignal signal)
        {
            var now = DateTime.UtcNow;
            // ime strategije (isti string koji pišemo u SignalRepo)
            var strategyName = _strategy.GetType().Name;
            void MarkBlocked(string shortReason)
            {
                try
                {
                    // Legacy metrika (bez phase)
                    StrategyMetrics.Instance.SignalBlocked(
                        strategyName,
                        signal.Symbol.Ticker,
                        shortReason
                    );
                    // Phase-aware metrika
                    StrategyMetrics.Instance.SignalBlockedByPhase(
                        strategyName,
                        signal.Symbol.Ticker,
                        shortReason,
                        now
                    );
                }
                catch
                { // metrics nikad ne sme da sruši engine
                }
            }

            var runEnv = _orderService is null ? "Paper" : "Real";
            var insideUsRthNow = TradingSessionGuard.IsInsideUsRth(now);
            var rthWindow = HasConfiguredTradingWindow()
                ? (insideUsRthNow && IsInsideConfiguredTradingWindow(now, out _) ? "inside" : "outside")
                : (insideUsRthNow ? "inside" : "outside");

            async Task LogSignalAsync(
                bool accepted,
                string? rejectReason,
                decimal? plannedQty = null,
                decimal? plannedNotional = null,
                string? corrId = null)
            {
                if (_signalRepo is null) return;

                try
                {
                    await _signalRepo.InsertAsync(
                        utc: now,
                        symbol: signal.Symbol.Ticker,
                        side: signal.Side.ToString(),
                        suggestedPrice: signal.SuggestedLimitPrice,
                        strategy: strategyName,
                        reason: signal.Reason,
                        accepted: accepted,
                        rejectReason: rejectReason,
                        plannedQty: plannedQty,
                        plannedNotional: plannedNotional,
                        correlationId: corrId,
                        runEnv: runEnv,
                        rthWindow: rthWindow,
                        exchange: signal.Symbol.Exchange,
                        ct: CancellationToken.None
                    );
                }
                catch
                {
                    try
                    {
                        AppMetrics.Instance.IncDbException();
                    }
                    catch
                    { // metrics nikad ne sme da sruši engine
                    }
                }
            }
            // =========================================================
            // 0) GLOBAL STRATEGY RATE-LIMIT
            // =========================================================
            if (IsRateLimited(now, out var rlReason))
            {
                _log.Warning(
                    "[RATE-LIMIT] blocked signal {Sym} reason={Reason}",
                    signal.Symbol.Ticker,
                    rlReason);

                MarkBlocked("rate-limit");
                _ = LogSignalAsync(false, $"rate-limit:{rlReason}");
                return;
            }
            // =========================================================
            // 1) HARD STOP - Equity Floor
            // =========================================================
            if (_equityFloorUsd > 0m)
            {
                var (equity0, inPosUsd) = GetApproxEquity();
                if (equity0 <= _equityFloorUsd)
                {
                    _log.Warning("[EQ-FLOOR] {Sym} equity {Eq:F2} (inPos={InPos:F2}) <= floor {Floor:F2}", signal.Symbol.Ticker, equity0, inPosUsd, _equityFloorUsd);
                    MarkBlocked("equity-floor");
                    _ = LogSignalAsync(false, "equity-floor");
                    return;
                }
            }
            // =========================================================
            // 2) Cooldown posle gubitka
            // =========================================================
            if (IsInCooldown(signal.Symbol, now, out var cooldownLeft))
            {
                MarkBlocked("cooldown");
                _ = LogSignalAsync(false, $"cooldown:{(int)cooldownLeft.TotalSeconds}");
                return;
            }
            // =========================================================
            // 3) Mora da postoji "zdrav" quote (fresh + NBBO + spread ok)
            // =========================================================
            if (!TryGetQuote(signal.Symbol.Ticker, now, out var q, out var liqReason))
            {
                MarkBlocked("liquidity");
                _ = LogSignalAsync(false, $"liquidity:{liqReason}");
                return;
            }
            
            try
            {
                if (!_tradingEnabled)
                {
                    MarkBlocked("trading-disabled");
                    _ = LogSignalAsync(false, "trading-disabled");
                    return;
                }
                // =========================================================
                // 4) RTH window: (a) US berza 9:30-16:00 ET + praznici + early close,
                // (b) opciono uži trading window iz config-a (DST-safe NY local ili legacy UTC).
                // =========================================================
                if (HasConfiguredTradingWindow())
                {
                    // 4a) US market: praznici zatvoreni, early close 13:00 ET, inace 9:30-16:00 ET
                    if (!TradingSessionGuard.IsInsideUsRth(now))
                    {
                        _log.Information(
                            "[RTH-BLOCK] {Sym} signal blocked OUTSIDE RTH (US market hours / holiday / early close) now={Now:O}",
                            signal.Symbol.Ticker, now);

                        MarkBlocked("outside-rth");
                        _ = LogSignalAsync(false, "outside-rth");
                        return;
                    }

                    // 4b) Postuj config prozor - preferiraj DST-safe NY local rules kada postoje.
                    if (!IsInsideConfiguredTradingWindow(now, out var windowDetails))
                    {
                        _log.Information(
                            "[RTH-BLOCK] {Sym} signal blocked OUTSIDE configured trading window now={Now:O} details={Details}",
                            signal.Symbol.Ticker, now, windowDetails);

                        MarkBlocked("outside-rth");
                        _ = LogSignalAsync(false, "outside-rth");
                        return;
                    }
                }

                var quoteTs = q.TimestampUtc == default ? DateTime.UtcNow : q.TimestampUtc;

                // WEEKEND GAP guard
                if (!TradingSessionGuard.IsWeekendGapClosed(quoteTs))
                {
                    _log.Information("[STR-GAP-BLOCK] {Sym} signal blocked WEEKEND TIME ts={NowUtc:o}", signal.Symbol.Ticker, quoteTs);
                    MarkBlocked("outside-working-days");
                    _ = LogSignalAsync(false, "outside-working-days");
                    return;
                }

                // =========================================================
                // 4.5) Macro trend gate (logs into trade_signals.reject_reason)
                // =========================================================
                if (_trendContextProvider is not null)
                {
                    try
                    {
                        var trend = _trendContextProvider
                            .GetTrendContextAsync(
                                exchange: signal.Symbol.Exchange ?? "IBKR",
                                symbol: signal.Symbol.Ticker,
                                quoteTsUtc: quoteTs,
                                ct: CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();

                        if (trend is null)
                        {
                            MarkBlocked("macro-trend-block");
                            _ = LogSignalAsync(false, "macro-trend-block:unknown:no-data");
                            return;
                        }

                        if (trend.LocalQuality is { Enabled: true, DryRun: true } lq)
                        {
                            _log.Information(
                                "[TREND-LQ-DRYRUN] {Sym} src={Src} macro={Macro:F6} mag30={Mag:F6} chop15Path={ChopPath:F6} chop15Eff={Eff:F4} ratio={Ratio} wouldPass={WouldPass} reason={Reason}",
                                signal.Symbol.Ticker,
                                lq.Source,
                                trend.Score,
                                lq.MagnitudeNetMoveFraction,
                                lq.ChopPathFraction,
                                lq.ChopEfficiency,
                                lq.ChopToMagnitudeRatio?.ToString("F2") ?? "n/a",
                                lq.WouldPass,
                                lq.DecisionReason);
                        }

                        if (trend.Direction != TrendDirection.Up)
                        {
                            var trendDirection = trend.Direction.ToString().ToLowerInvariant();
                            MarkBlocked("macro-trend-block");
                            _ = LogSignalAsync(false, $"macro-trend-block:{trendDirection}:score={trend.Score:F6}");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[TREND] Failed to evaluate macro trend for {Sym}", signal.Symbol.Ticker);
                        MarkBlocked("macro-trend-block");
                        _ = LogSignalAsync(false, "macro-trend-block:error");
                        return;
                    }
                }

                // =========================================================
                // 5) Prevent spam - vec ima pending order na simbolu
                // =========================================================
                // 5) Info-only: vec postoji pending order na simbolu, ali VISE NE BLOKIRAMO.
                // Razlog: previše agresivno ubija validne re-entry / scale-in setupe,
                // a realnu zastitu vec rade risk engine + exposure + cash floor + rate limit.
                var hasPending = HasPendingForSymbol(signal.Symbol.Ticker);
               if (hasPending)
               {
                   if (_orderService is null)
                   {
                       // PAPER mod: više ne blokiramo signal zbog pending-exists,
                       // samo logujemo da bismo videli ponašanje bez ove zaštite.
                       _log.Information("[PENDING-IGNORE] PAPER mode: pending-exists for {Sym}, ali NE blokiramo signal", signal.Symbol.Ticker );
                   }
                   else
                   {
                       // REAL mod: i dalje čuvamo zaštitu dok ne vidimo statistiku iz papira
                       MarkBlocked("pending-exists");
                       _ = LogSignalAsync(false, "pending-exists");
                       return;
                   }
               }

                // =========================================================
                // 5.5) MaxOnePositionPerSymbol - ne dodaj ako vec imas long
                // =========================================================
                var symbolPos = _positionBook.Get(signal.Symbol.Ticker);
                if (_swingConfig.MaxOnePositionPerSymbol)
                {
                    if (symbolPos != null && symbolPos.Quantity > 0m)
                    {
                        _log.Information(
                            "[BLOCK] {Sym} MaxOnePositionPerSymbol: vec ima long qty={Qty} avg={Avg:F2}, ignorisemo novi buy signal",
                            signal.Symbol.Ticker, symbolPos.Quantity, symbolPos.AveragePrice);
                        MarkBlocked("max-one-position-per-symbol");
                        _ = LogSignalAsync(false, "max-one-position-per-symbol");
                        return;
                    }
                }

                // =========================================================
                // 6) Risk engine (osnovni sizing)
                // =========================================================
                var cash = _lastCash;
                var rc = _risk.EvaluateEntry(signal, cash, _limits, _fees, _perSymbolBudgetUsd);

                if (!rc.Allowed || rc.Quantity <= 0m)
                {
                    MarkBlocked("risk");
                    _ = LogSignalAsync(false, $"risk:{rc.Reason}");
                    return;
                }

                var px = signal.SuggestedLimitPrice ?? 0m;
                if (px <= 0m)
                {
                    MarkBlocked("invalid-price");
                    _ = LogSignalAsync(false, "invalid-price");
                    return;
                }

                // 6.05) Quality-aware slot consumption:
                // drugi i poslednji dnevni slot traže jači signal score od prvog slot-a.
                var currentTradeCountTotal = _dayGuards?.CurrentTradeCountTotal ?? 0;
                if (TryGetRequiredSignalPriorityScore(currentTradeCountTotal, out var requiredPriorityScore))
                {
                    var signalPriorityScore = ComputeSignalPriorityScore(signal);
                    if (signalPriorityScore.HasValue)
                    {
                        _log.Information(
                            "[SIGNAL-PRIORITY] {Sym} score={Score:F1} required={Required:F1} currentTotal={Current} maxTotal={Max}",
                            signal.Symbol.Ticker,
                            signalPriorityScore.Value,
                            requiredPriorityScore,
                            currentTradeCountTotal,
                            _settings.MaxTradesTotal);

                        if (signalPriorityScore.Value < requiredPriorityScore)
                        {
                            MarkBlocked("signal-priority");
                            _ = LogSignalAsync(false, $"signal-priority:{signalPriorityScore.Value:F1}<{requiredPriorityScore:F1}");
                            return;
                        }
                    }
                    else
                    {
                        _log.Warning(
                            "[SIGNAL-PRIORITY] {Sym} missing priority metadata; allowing signal without score gate currentTotal={Current} maxTotal={Max}",
                            signal.Symbol.Ticker,
                            currentTradeCountTotal,
                            _settings.MaxTradesTotal);
                    }
                }

                // 6.1) Day-guards: max trades / daily loss lock
                if (_dayGuards is not null)
                {
                    var currentTotal = _dayGuards.CurrentTradeCountTotal;
                    var currentPerSymbol = _dayGuards.CurrentTradeCountPerSymbol.TryGetValue(signal.Symbol.Ticker, out var symCount) ? symCount : 0;
                    
                    if (!_dayGuards.CanTrade(signal.Symbol, now, out var dgReason))
                    {
                        _log.Warning("[DAY-GUARD] Blocked entry for {Sym}. Reason={Reason} (Total={Total}/{MaxTotal} PerSymbol={PerSym}/{MaxPerSym})", 
                            signal.Symbol.Ticker, dgReason ?? "n/a", currentTotal, currentPerSymbol);
                        MarkBlocked("day-guard");
                        _ = LogSignalAsync(false, $"day-guard:{dgReason}");
                        return;
                    }
                    else
                    {
                        _log.Debug("[DAY-GUARD] Allowed entry for {Sym} (Total={Total}/{MaxTotal} PerSymbol={PerSym}/{MaxPerSym})", 
                            signal.Symbol.Ticker, currentTotal, currentPerSymbol);
                    }
                }

                // =========================================================
                // 6.2) ATR-based risk sizing - adaptivno ogranicavanje qty
                // =========================================================
                // Kreiraj correlationId ranije da ga možemo koristiti za sizing metadata
                var correlationId = $"sig-{signal.Symbol.Ticker}-{Guid.NewGuid():N}";
                
                decimal? atrOpt = null;
                lock (_sync)
                {
                    if (_atr.TryGetValue(signal.Symbol.Ticker, out var s) && s.Atr.HasValue && s.Atr.Value > 0m) {atrOpt = s.Atr.Value;}
                }
                // efektivni risk fraction: kombinacija globalnog limita i swing-configa
                decimal riskFraction = _limits.MaxRiskPerTradeFraction;

                // ako smo u SWING modu i u configu postoji MaxSingleTradeRiskPct - ukljuci ga
                if (SwingHelpers.IsSwingMode(_swingConfig) && _swingConfig.MaxSingleTradeRiskPct > 0m)
                {
                    if (riskFraction > 0m)
                    {
                        riskFraction = Math.Min(riskFraction, _swingConfig.MaxSingleTradeRiskPct);
                    }
                    else
                    {
                        riskFraction = _swingConfig.MaxSingleTradeRiskPct;
                    }
                }

                if (atrOpt.HasValue && riskFraction > 0m)
                {
                    var slAtrMultiple = SlAtrMultiple; // koliko ATR-a uzimamo kao "risk distance" za SL
                    var atr = atrOpt.Value;

                    // approx equity
                    var equityAtr = cash.Free + cash.Settling + cash.InPositions;
                    var riskPerTradeUsd = equityAtr * riskFraction; // npr. 1% u swing modu

                    // koliko smo spremni da izgubimo po jednoj akciji (price risk)
                    var pctSlDistance = px * _slFraction; // % SL (0.75%)
                    var atrSlDistance = slAtrMultiple * atr; // ATR-based SL
                    var priceRisk = Math.Max(pctSlDistance, atrSlDistance);

                    if (priceRisk > 0m && riskPerTradeUsd > 0m)
                    {
                        var qtyByRisk = riskPerTradeUsd / priceRisk;
                        var adjQty = Math.Min(rc.Quantity, qtyByRisk);

                        if (adjQty <= 0m)
                        {
                            _log.Information(
                                "[RISK-SIZE] Blocked {Sym} qtyByRisk<=0 (eq={Eq:F2}, riskPerTrade={Risk:F2}, priceRisk={PR:F4}, frac={Frac:P2})",
                                signal.Symbol.Ticker, equityAtr, riskPerTradeUsd, priceRisk, riskFraction);

                            MarkBlocked("atr-risk");
                            _ = LogSignalAsync(false, "atr-risk-sizer-zero");
                            return;
                        }

                        _log.Information(
                            "[RISK-SIZE] {Sym} qty adj old={Old:F6} new={New:F6} atr={Atr:F4} px={Px:F2} riskPerTrade={Risk:F2} priceRisk={PR:F4} frac={Frac:P2}",
                            signal.Symbol.Ticker, rc.Quantity, adjQty, atr, px, riskPerTradeUsd, priceRisk, riskFraction);

                        // Sačuvaj metadata za TradeJournalEntry
                        lock (_sync)
                        {
                            _sizingMetadata[correlationId] = (riskFraction, atr, priceRisk);
                        }

                        rc = rc with { Quantity = adjQty };
                    }
                    else
                    {
                        // Ako nema ATR sizing, sačuvaj samo riskFraction
                        lock (_sync)
                        {
                            _sizingMetadata[correlationId] = (riskFraction, null, null);
                        }
                    }
                }
                else
                {
                    // Ako nema ATR, sačuvaj samo riskFraction (ako postoji)
                    if (riskFraction > 0m)
                    {
                        lock (_sync)
                        {
                            _sizingMetadata[correlationId] = (riskFraction, null, null);
                        }
                    }
                }

                // =========================================================
                // 7) Symbol exposure guard - MaxExposurePerSymbolFrac
                //     dozvoli scale-in dok je ispod cap-a
                // =========================================================
                var exposureEquity = cash.Free + cash.Settling + cash.InPositions;
                if (exposureEquity <= 0m)
                {
                    MarkBlocked("no-equity");
                    _ = LogSignalAsync(false, "no-equity");
                    return;
                }

                var maxSymbolExposure = exposureEquity * _limits.MaxExposurePerSymbolFrac;
                var symbolNotional = symbolPos is null ? 0m : symbolPos.Quantity * px;
                var plannedNotional = rc.Quantity * px;
                // vec si pun po simbolu
                if (symbolNotional >= maxSymbolExposure)
                {
                    _log.Information("[BLOCK] {Sym} symbol exposure full. existing={Existing:F2} cap={Cap:F2}", signal.Symbol.Ticker, symbolNotional, maxSymbolExposure);
                    MarkBlocked("symbol-exposure-full");
                    _ = LogSignalAsync(false, "symbol-exposure-full");
                    return;
                }

                // probijas cap - probaj da smanjis kolicinu
                if (symbolNotional + plannedNotional > maxSymbolExposure)
                {
                    var remaining = maxSymbolExposure - symbolNotional;
                    var cappedQty = remaining / px;

                    if (cappedQty <= 0m)
                    {
                        _log.Information("[BLOCK] {Sym} exposure cap reached. existing={Existing:F2} cap={Cap:F2}", signal.Symbol.Ticker, symbolNotional, maxSymbolExposure);
                        MarkBlocked("symbol-exposure-cap");
                        _ = LogSignalAsync(false, "symbol-exposure-cap");
                        return;
                    }

                    _log.Information("[ADJUST] {Sym} qty capped by symbol exposure. oldQty={OldQty:F6} newQty={NewQty:F6}", signal.Symbol.Ticker, rc.Quantity, cappedQty);
                    rc = rc with { Quantity = cappedQty };
                }

                // =========================================================
                // 8) Normalize quantity - BEZ force-min, po broker pravilima
                var rawQty = rc.Quantity;
                var qty = NormalizeQty(signal.Symbol, rawQty);

                if (qty <= 0)
                {
                    // PAPER fallback:
                    // U paper modu NECEMO baciti signal, nego cemo forsirati minimalnu kolicinu
                    // da vidimo kako bi se strategija ponašala bez normalized-zero bloka.
                    if (_orderService is null)
                    {
                        var cfgSym = _settings.Symbols?.FirstOrDefault(s => string.Equals(s.Symbol, signal.Symbol.Ticker, StringComparison.OrdinalIgnoreCase));
                        var min = cfgSym?.MinQty > 0m ? cfgSym.MinQty : 1m;
                        qty = min;
                        _log.Information("[NORM-FALLBACK] PAPER mode: overriding normalized-zero for {Sym}, rawQty={Raw:F6} -> qty={Qty:F6}", signal.Symbol.Ticker, rawQty, qty );
                    }

                    if (qty <= 0)
                    {
                        MarkBlocked("normalized-zero");
                        _ = LogSignalAsync(false, "normalized-zero");
                        return;
                    }
                }
                rc = rc with { Quantity = qty };
                
                // =========================================================
                // 8.5) Minimum quantity check (IBKR only) - config Trading.MinQuantity
                //      Blokira entry ako je qty ispod praga (npr. skupi simboli, mali broj akcija).
                // =========================================================
                if (_settings.MinQuantity > 0 && qty < _settings.MinQuantity)
                {
                    _log.Information("[MIN-QTY] Blocked {Sym} qty={Qty:F6} < min={Min}", 
                        signal.Symbol.Ticker, qty, _settings.MinQuantity);
                    MarkBlocked("min-quantity");
                    _ = LogSignalAsync(false, $"min-quantity:{qty:F6}<{_settings.MinQuantity}");
                    return;
                }
                
                // =========================================================
                // 9) Real cash check + min free cash floor
                // =========================================================
                var totalCost = qty * px + _fees.EstimatedPerOrderUsd;

                if (_orderService != null)
                {
                    // 9a) uopšte dovoljno keša za trade
                    if (cash.Free < totalCost)
                    {
                        MarkBlocked("not-enough-cash");
                        _ = LogSignalAsync(false, "not-enough-cash");
                        return;
                    }

                    // 9b) posle trade-a mora da ostane bar _minFreeCashUsd
                    if (_minFreeCashUsd > 0m)
                    {
                        var freeAfter = cash.Free - totalCost;
                        if (freeAfter < _minFreeCashUsd)
                        {
                            _log.Warning("[CASH-FLOOR] {Sym} freeAfter={FreeAfter:F2} < floor={Floor:F2} freeBefore={Free:F2} cost={Cost:F2}",
                                signal.Symbol.Ticker,
                                freeAfter,
                                _minFreeCashUsd,
                                cash.Free,
                                totalCost
                            );
                            MarkBlocked("cash-floor");
                            _ = LogSignalAsync(false, "cash-floor");
                            return;
                        }
                    }
                    SafeReserve(totalCost, now, "entry");
                }
                // =========================================================
                // 10) Create + place order
                // =========================================================
                // correlationId je vec kreiran ranije za sizing metadata
                var corr = correlationId;

                // Prometheus: BROJ PRIHVACENIH (executed) BUY/SELL signala
                try
                {
                    var sym = signal.Symbol.Ticker;
                    if (signal.Side == OrderSide.Buy)
                    {
                        OrderMetrics.Instance.SignalBuy(sym);
                    }
                    else if (signal.Side == OrderSide.Sell)
                    {
                        OrderMetrics.Instance.SignalSell(sym);
                    }
                }
                catch
                {
                    // metrics nikad ne sme da sruši trading engine
                }

                // 10.1) Upis u broker_orders (ENTRY nalog)
                if (_orderRepo is not null)
                {
                    try
                    {
                        decimal? bidSnap = q.Bid;
                        decimal? askSnap = q.Ask;
                        decimal? spreadSnap = (bidSnap.HasValue && askSnap.HasValue)
                            ? askSnap - bidSnap
                            : null;

                        _ = _orderRepo.InsertSubmittedAsync(
                           id: corr,
                           symbol: signal.Symbol.Ticker,
                           side: signal.Side.ToString(),
                           qty: qty,
                           orderType: "limit",
                           limitPrice: px > 0 ? px : null,
                           stopPrice: null,
                           createdUtc: now,
                           submitBid: bidSnap,
                           submitAsk: askSnap,
                           submitSpread: spreadSnap,
                           exchange: signal.Symbol.Exchange,
                           ct: CancellationToken.None
                       );
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[DB-ORDERS] InsertSubmitted failed corr={Corr}", corr);
                    }
                }

                var req = new OrderRequest(
                    symbol: signal.Symbol,
                    side: signal.Side,
                    type: OrderType.Limit,
                    quantity: qty,
                    limitPrice: px,
                    tif: TimeInForce.Day,
                    correlationId: corr,
                    timestampUtc: now
                );

                // obeleži da je nalog poslat (paper ili real)
                if (_dayGuards != null)
                {
                    var beforeTotal = _dayGuards.CurrentTradeCountTotal;
                    var beforePerSym = _dayGuards.CurrentTradeCountPerSymbol.TryGetValue(signal.Symbol.Ticker, out var symCount) ? symCount : 0;
                    _dayGuards.OnOrderPlaced(signal.Symbol, now);
                    var afterTotal = _dayGuards.CurrentTradeCountTotal;
                    var afterPerSym = _dayGuards.CurrentTradeCountPerSymbol.TryGetValue(signal.Symbol.Ticker, out var symCount2) ? symCount2 : 0;
                    _log.Information("[DAY-GUARD-COUNT] OnOrderPlaced for {Sym}: Total {Before}->{After} PerSymbol {BeforeSym}->{AfterSym}", 
                        signal.Symbol.Ticker, beforeTotal, afterTotal, beforePerSym, afterPerSym);
                }

                _orders.TryAdd(new PendingOrder(req, totalCost, now, BrokerOrderId: null));

                _ = LogSignalAsync(true, null, qty, qty * px, corr);

                if (_orderService == null)
                {
                    _paperSim.Register(req);
                }
                else
                {
                    _ = PlaceRealAsync(req);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[ERROR]");
                _ = LogSignalAsync(false, "exception");

                try
                {
                    MarkBlocked("exception");
                }
                catch
                {//ignore
                }

                try
                {
                    AppMetrics.Instance.IncGeneralException();
                }
                catch
                { // metrics nikad ne sme da sruši engine
                }
            }
        }
        // =========================
        //  PAPER FILLS
        // =========================
        private void OnPaperFilled(OrderRequest req, decimal fillPx)
        {
            var now = DateTime.UtcNow;
            // standardni put
            ApplyFillCore(req, fillPx, now, isPaper: true);

            // i "isti" fee kao u realu - da paper bude sto slicniji
            var estFee = _fees.EstimatedPerOrderUsd;
            if (estFee > 0m)
            {
                _cashService.OnCommissionPaid(estFee);
                _log.Information("[PAPER-FEE] {Sym} estFee={Fee:F2}", req.Symbol.Ticker, estFee);

                // (test) upiši fee u daily_pnl i u paper modu
                if (_pnlRepo is not null)
                {
                    try
                    {
                        _ = _pnlRepo.AddFeeAsync(now, estFee, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[DB-PNL] add fee (paper) failed");
                    }
                }
            }
        }
        // =========================
        //  FILL CORE
        // =========================
        private void ApplyFillCore(OrderRequest req, decimal fillPx, DateTime utcNow, bool isPaper)
        {
            _log.Debug(
                "[APPLY-FILL-CORE] START sym={Sym} side={Side} qty={Qty} px={Px} isExit={IsExit} isPaper={Paper} corr={Corr}",
                req.Symbol.Ticker, req.Side, req.Quantity, fillPx, req.IsExit, isPaper, req.CorrelationId);
            
            // stvarni promet (za ovaj slice)
            var notional = req.Quantity * fillPx;

            // pre fill-a: zapamti stanje pozicije
            var prevPos = _positionBook.Get(req.Symbol.Ticker);
            var prevQty = prevPos?.Quantity ?? 0m;
            
            _log.Debug(
                "[APPLY-FILL-CORE] BEFORE sym={Sym} prevQty={PrevQty} side={Side}",
                req.Symbol.Ticker, prevQty, req.Side);

            // 1) pozicije + cash - ovde dobijamo realizedPnl
            decimal realizedPnl;

            if (req.Side == OrderSide.Buy)
            {
                realizedPnl = _positionBook.ApplyBuyFill(req.Symbol.Ticker, req.Quantity, fillPx);
                _cashService.OnBuyFilled(notional, utcNow);
                TrackDiscordFillNotification(req, notional, realizedPnl, fillPx, isPaper, exitReason: null);

                // posle BUY fill-a: ako smo upravo otvorili novu poziciju (pre je bilo 0, sada > 0)
                var newPos = _positionBook.Get(req.Symbol.Ticker);
                var newQty = newPos?.Quantity ?? 0m;

                if (prevQty <= 0m && newQty > 0m)
                {
                    // Determine regime and symbol baseline at entry (frozen snapshot)
                    string? regime = null;
                    string? symbolBaseline = null;
                    decimal? atrAtEntry = null;
                    
                    lock (_sync)
                    {
                        if (_atr.TryGetValue(req.Symbol.Ticker, out var atrState) && atrState.Atr.HasValue && fillPx > 0m)
                        {
                            var rawAtr = atrState.Atr.Value;
                            var minAtrFrac = _limits.MinAtrFraction;
                            var minAtrAbs = fillPx * minAtrFrac;
                            var flooredAtr = Math.Max(rawAtr, minAtrAbs);
                            atrAtEntry = flooredAtr;
                            
                            // Determine regime based on ATR fraction
                            var atrFrac = flooredAtr / fillPx;
                            if (atrFrac > 0.0005m) regime = "HIGH";
                            else if (atrFrac > 0.00015m) regime = "NORMAL";
                            else regime = "LOW";
                            
                            // Determine symbol baseline (simple heuristic: based on ATR fraction)
                            // slow: < 0.0002, normal: 0.0002-0.0005, fast: > 0.0005
                            if (atrFrac < 0.0002m) symbolBaseline = "slow";
                            else if (atrFrac <= 0.0005m) symbolBaseline = "normal";
                            else symbolBaseline = "fast";
                        }
                        
                        _posRuntime[req.Symbol.Ticker] = new PositionRuntimeState
                        {
                            EntryUtc = utcNow,
                            EntryPrice = fillPx,
                            BestPrice = fillPx,
                            IsExternal = false,
                            RegimeAtEntry = regime ?? "LOW", // default to LOW if ATR not available
                            SymbolBaseline = symbolBaseline ?? "normal", // default to normal
                            AtrAtEntry = atrAtEntry
                        };
                    }
                }

                // --- SWING DB: upsert open pozicije ---
                // Bitno: OpenedUtc NE SME da se resetuje na scale-in, pa uzmi prvi EntryUtc iz _posRuntime ako postoji.
                if (!isPaper && _swingPosRepo is not null && SwingHelpers.IsSwingMode(_swingConfig) && newQty > 0m)
                {
                    try
                    {
                        var openedUtc = utcNow;
                        lock (_sync)
                        {
                            if (_posRuntime.TryGetValue(req.Symbol.Ticker, out var rt))
                                openedUtc = rt.EntryUtc;
                        }

                        var snap = new SwingPositionSnapshot
                        {
                            Symbol = req.Symbol.Ticker,
                            Quantity = newQty,
                            EntryPrice = newPos?.AveragePrice ?? fillPx,
                            OpenedUtc = openedUtc,
                            Strategy = _strategy.GetType().Name,
                            CorrelationId = req.CorrelationId ?? string.Empty,
                            PlannedHoldingDays = _swingConfig.MaxHoldingDays,
                            ExitPolicy = SwingExitPolicy.PriceOrTime
                        };

                        _ = _swingPosRepo.UpsertOpenAsync(snap, exchange: req.Symbol.Exchange ?? "SMART", CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[SWING-DB] UpsertOpen failed for {Sym}", req.Symbol.Ticker);
                    }
                }

                // ========== AUTO-OCO nakon BUY otvaranja pozicije ==========
                // Kreiraj OCO ako:
                // 1) Nema prethodne pozicije (prevQty <= 0) -> pokrij ceo newQty
                // 2) Scale-in (newQty > prevQty) -> pokrij samo nepokrivenu kolicinu (delta)
                bool shouldCreateOco = false;
                decimal ocoQty = 0m;
                if (!isPaper && newQty > 0m)
                {
                    if (prevQty <= 0m)
                    {
                        // Nova pozicija - uvek kreiraj OCO
                        shouldCreateOco = true;
                        ocoQty = newQty;
                    }
                    else if (newQty > prevQty)
                    {
                        // Ako je vec u toku EXIT workflow (manual/forced), ne dodaj nove OCO naloge.
                        bool exitInProgress = false;
                        lock (_sync)
                        {
                            exitInProgress = _exitPending.Contains(req.Symbol.Ticker);
                        }

                        if (exitInProgress)
                        {
                            _log.Information(
                                "[OCO-SCALE-IN] Skipping OCO creation - exit is already pending for {Sym}",
                                req.Symbol.Ticker);
                        }
                        else
                        {
                            // Proveri koliko je vec pokriveno pending exit nalozima.
                            // Grupišemo po OCO grupi (ili correlationId fallback) da ne dupliramo TP/SL/SL-ORTH iste grupe.
                            var pendingExits = _orders.Snapshot()
                                .Where(po =>
                                    po.Req.Symbol.Ticker.Equals(req.Symbol.Ticker, StringComparison.OrdinalIgnoreCase) &&
                                    po.Req.IsExit &&
                                    (po.Req.CorrelationId?.StartsWith("exit-", StringComparison.OrdinalIgnoreCase) ?? false))
                                .ToList();

                            decimal protectedQty = 0m;
                            if (pendingExits.Count > 0)
                            {
                                protectedQty = pendingExits
                                    .GroupBy(
                                        po => string.IsNullOrWhiteSpace(po.Req.OcoGroupId)
                                            ? po.CorrelationId
                                            : po.Req.OcoGroupId!,
                                        StringComparer.OrdinalIgnoreCase)
                                    .Sum(g => g.Max(po => po.Req.Quantity));
                            }

                            var missingQty = newQty - protectedQty;
                            if (missingQty > 0m)
                            {
                                shouldCreateOco = true;
                                ocoQty = missingQty;

                                _log.Information(
                                    "[OCO-SCALE-IN] Creating delta OCO sym={Sym} prevQty={PrevQty:F6} newQty={NewQty:F6} protectedQty={ProtectedQty:F6} missingQty={MissingQty:F6}",
                                    req.Symbol.Ticker, prevQty, newQty, protectedQty, missingQty);
                            }
                            else
                            {
                                _log.Information(
                                    "[OCO-SCALE-IN] Skipping OCO creation - already protected sym={Sym} prevQty={PrevQty:F6} newQty={NewQty:F6} protectedQty={ProtectedQty:F6}",
                                    req.Symbol.Ticker, prevQty, newQty, protectedQty);
                            }
                        }
                    }
                }

                if (shouldCreateOco && ocoQty > 0m)
                {
                    try
                    {
                        var sym = req.Symbol;
                        var entryPx = fillPx;

                        // isti OCO/OCA group id za sve exit naloge (TP, SL-RTH, opcioni SL-ORTH)
                        var ocoId = $"OCO-{sym.Ticker}-{Guid.NewGuid():N}";

                        // ATR lookup (ComputeTpSlLevels vec radi floor na % cene)
                        // Smanji TP za vece kolicine (manje reward, manje rizika)
                        var (tpPx, slPx, atrUsed) = ComputeTpSlLevels(sym.Ticker, entryPx);

                        // Swing: GTC, intraday: DAY
                        var exitTif = SwingHelpers.IsSwingMode(_swingConfig)
                            ? TimeInForce.Gtc
                            : TimeInForce.Day;

                        var tpReq = new OrderRequest(
                            symbol: sym,
                            side: OrderSide.Sell,
                            type: OrderType.Limit,
                            quantity: ocoQty,
                            limitPrice: tpPx,
                            tif: exitTif,
                            correlationId: $"exit-tp-{Guid.NewGuid():N}",
                            timestampUtc: utcNow,
                            ocoGroupId: ocoId,
                            ocoStopPrice: null,
                            stopPrice: null,
                            isExit: true
                        );

                        var slReq = new OrderRequest(
                            symbol: sym,
                            side: OrderSide.Sell,
                            type: OrderType.Stop,
                            quantity: ocoQty,
                            limitPrice: null,
                            tif: exitTif,
                            correlationId: $"exit-sl-{Guid.NewGuid():N}",
                            timestampUtc: utcNow,
                            ocoGroupId: ocoId,
                            ocoStopPrice: null,
                            stopPrice: slPx,
                            isExit: true
                        );

                        // Opcioni van-RTH stop-limit nalog (best-effort zastita).
                        // Mapper ga prepoznaje po correlationId prefiksu "exit-sl-orth-".
                        OrderRequest? slOrthReq = null;
                        if (_settings.EnableStopLimitOutsideRth)
                        {
                            var orthSlip = Math.Max(slPx * 0.01m, 0.01m); // 1% ili minimum $0.01
                            var orthLimit = slPx - orthSlip;

                            slOrthReq = new OrderRequest(
                                symbol: sym,
                                side: OrderSide.Sell,
                                type: OrderType.Stop,
                                quantity: ocoQty,
                                limitPrice: orthLimit,
                                tif: exitTif,
                                correlationId: $"exit-sl-orth-{Guid.NewGuid():N}",
                                timestampUtc: utcNow,
                                ocoGroupId: ocoId,
                                ocoStopPrice: null,
                                stopPrice: slPx,
                                isExit: true
                            );
                        }

                        if (slOrthReq is not null)
                        {
                            _log.Information(
                                "[OCO] Creating OCO3 group={Oco} TP={TP:F2} SL-RTH={SL:F2} SL-ORTH=enabled sym={Sym} qty={Qty} positionQty={PosQty}, exitTif={ExitTif}",
                                ocoId, tpPx, slPx, sym.Ticker, ocoQty, newQty, exitTif);
                        }
                        else
                        {
                            _log.Information(
                                "[OCO] Creating OCO group={Oco} TP={TP:F2} SL={SL:F2} sym={Sym} qty={Qty} positionQty={PosQty}, exitTif={ExitTif}",
                                ocoId, tpPx, slPx, sym.Ticker, ocoQty, newQty, exitTif);
                        }

                        // --- broker_orders: insert submitted za TP, SL-RTH i opcioni SL-ORTH ---
                        if (_orderRepo is not null)
                        {
                            try
                            {
                                decimal? submitBid = null, submitAsk = null, submitSpread = null;

                                lock (_sync)
                                {
                                    if (_lastQuotes.TryGetValue(sym.Ticker, out var snap) &&
                                        snap is not null &&
                                        snap.Bid.HasValue && snap.Ask.HasValue &&
                                        snap.Bid > 0m && snap.Ask > 0m)
                                    {
                                        submitBid = snap.Bid;
                                        submitAsk = snap.Ask;
                                        submitSpread = snap.Ask - snap.Bid;
                                    }
                                }

                                _ = _orderRepo.InsertSubmittedAsync(
                                  id: tpReq.CorrelationId,
                                  symbol: sym.Ticker,
                                  side: "Sell",
                                  qty: ocoQty,
                                  orderType: "limit",
                                  limitPrice: tpPx,
                                  stopPrice: null,
                                  createdUtc: utcNow,
                                  submitBid: submitBid,
                                  submitAsk: submitAsk,
                                  submitSpread: submitSpread,
                                  exchange: sym.Exchange,
                                  ct: CancellationToken.None
                              );

                                // SL
                                _ = _orderRepo.InsertSubmittedAsync(
                                     id: slReq.CorrelationId,
                                     symbol: sym.Ticker,
                                     side: "Sell",
                                     qty: ocoQty,
                                     orderType: "stop",
                                     limitPrice: null,
                                     stopPrice: slPx,
                                     createdUtc: utcNow,
                                     submitBid: submitBid,
                                     submitAsk: submitAsk,
                                     submitSpread: submitSpread,
                                     exchange: sym.Exchange,
                                     ct: CancellationToken.None
                                 );

                                if (slOrthReq is not null)
                                {
                                    _ = _orderRepo.InsertSubmittedAsync(
                                        id: slOrthReq.CorrelationId,
                                        symbol: sym.Ticker,
                                        side: "Sell",
                                        qty: ocoQty,
                                        orderType: "stop_limit",
                                        limitPrice: slOrthReq.LimitPrice,
                                        stopPrice: slPx,
                                        createdUtc: utcNow,
                                        submitBid: submitBid,
                                        submitAsk: submitAsk,
                                        submitSpread: submitSpread,
                                        exchange: sym.Exchange,
                                        ct: CancellationToken.None
                                    );
                                }

                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "[DB-ORDERS] InsertSubmitted OCO failed sym={Sym}", sym.Ticker);
                            }
                        }

                        // pending store
                        _orders.TryAdd(new PendingOrder(tpReq, _fees.EstimatedPerOrderUsd, utcNow));
                        _orders.TryAdd(new PendingOrder(slReq, _fees.EstimatedPerOrderUsd, utcNow));
                        if (slOrthReq is not null)
                            _orders.TryAdd(new PendingOrder(slOrthReq, _fees.EstimatedPerOrderUsd, utcNow));

                        _ = PlaceRealAsync(tpReq);
                        _ = PlaceRealAsync(slReq);
                        if (slOrthReq is not null)
                            _ = PlaceRealAsync(slOrthReq);
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "[OCO] Failed to place OCO group for {Sym}", req.Symbol.Ticker);
                    }
                }
            }
            else
            {
                _log.Debug(
                    "[APPLY-FILL-CORE] SELL fill sym={Sym} qty={Qty} px={Px} isExit={IsExit} corr={Corr}",
                    req.Symbol.Ticker, req.Quantity, fillPx, req.IsExit, req.CorrelationId);
                
                realizedPnl = _positionBook.ApplySellFill(req.Symbol.Ticker, req.Quantity, fillPx);
                _cashService.OnSellProceeds(notional, utcNow);

                // ako smo imali reserved exposure - pusti ga (po slice-u)
                _exposure?.Release(req.Symbol, notional);

                // posle SELL fill-a: proveri da li je pozicija STVARNO zatvorena
                var newPos = _positionBook.Get(req.Symbol.Ticker);
                var newQty = newPos?.Quantity ?? 0m;
                
                _log.Debug(
                    "[APPLY-FILL-CORE] SELL after fill sym={Sym} prevQty={PrevQty} newQty={NewQty} realizedPnl={Pnl:F2}",
                    req.Symbol.Ticker, prevQty, newQty, realizedPnl);

                if (newQty <= 0m)
                {
                    lock (_sync)
                    {
                        _exitPending.Remove(req.Symbol.Ticker);
                        _lastExitUtc[req.Symbol.Ticker] = utcNow;
                        _posRuntime.Remove(req.Symbol.Ticker);
                    }
                }

                // Discord notification for SELL
                string? exitReasonStr = null;
                if (newQty <= 0m && prevQty > 0m)
                {
                    if (!isPaper && _swingPosRepo is not null && SwingHelpers.IsSwingMode(_swingConfig))
                    {
                        var (exitReasonOpt, _) = SwingHelpers.InferSwingExitReason(req);
                        exitReasonStr = exitReasonOpt?.ToString() ?? "Manual";
                    }
                    else
                    {
                        exitReasonStr = "Position Closed";
                    }
                }

                TrackDiscordFillNotification(req, notional, realizedPnl, fillPx, isPaper, exitReasonStr);

                // SWING DB: zatvori samo kad je pozicija stvarno zatvorena (i bila je otvorena pre toga)
                if (!isPaper &&
                    _swingPosRepo is not null &&
                    SwingHelpers.IsSwingMode(_swingConfig) &&
                    prevQty > 0m &&
                    newQty <= 0m)
                {
                    _log.Information(
                        "[SWING-DB-CHECK] Position closed detected sym={Sym} prevQty={PrevQty} newQty={NewQty} isExit={IsExit} corr={Corr}",
                        req.Symbol.Ticker, prevQty, newQty, req.IsExit, req.CorrelationId);
                    
                    try
                    {
                        var (exitReasonOpt, autoExit) = SwingHelpers.InferSwingExitReason(req);

                        var exitReason = exitReasonOpt ?? SwingExitReason.Manual; // default fallback

                        _log.Information(
                            "[SWING-DB-CALL] Calling MarkClosedAsync sym={Sym} exchange={Ex} exitReason={Reason} auto={Auto}",
                            req.Symbol.Ticker, req.Symbol.Exchange ?? "SMART", exitReason, autoExit);

                        var t = _swingPosRepo.MarkClosedAsync(
                            symbol: req.Symbol.Ticker,
                            exchange: req.Symbol.Exchange ?? "SMART",
                            closedUtc: utcNow,
                            exitReason: exitReason,
                            autoExit: autoExit,
                            ct: CancellationToken.None);

                        _ = t.ContinueWith(
                            tt =>
                            {
                                if (tt.IsFaulted)
                                {
                                    _log.Error(tt.Exception, "[SWING-DB-ERROR] MarkClosedAsync faulted {Sym}", req.Symbol.Ticker);
                                }
                                else
                                {
                                    _log.Information("[SWING-DB-SUCCESS] MarkClosedAsync completed {Sym}", req.Symbol.Ticker);
                                }
                            });
                        _log.Information(
                            "[SWING-DB] MarkClosed {Sym} exitReason={Reason} auto={Auto}",
                            req.Symbol.Ticker,
                            exitReason,
                            autoExit);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[SWING-DB] MarkClosed scheduling failed for {Sym}", req.Symbol.Ticker);
                    }
                }
                else
                {
                    if (isPaper)
                        _log.Debug("[SWING-DB-SKIP] Skipping MarkClosed - paper mode sym={Sym}", req.Symbol.Ticker);
                    else if (_swingPosRepo is null)
                        _log.Debug("[SWING-DB-SKIP] Skipping MarkClosed - _swingPosRepo is NULL sym={Sym}", req.Symbol.Ticker);
                    else if (!SwingHelpers.IsSwingMode(_swingConfig))
                        _log.Debug("[SWING-DB-SKIP] Skipping MarkClosed - not swing mode sym={Sym}", req.Symbol.Ticker);
                    else if (prevQty <= 0m)
                        _log.Debug("[SWING-DB-SKIP] Skipping MarkClosed - prevQty={PrevQty} <= 0 sym={Sym}", prevQty, req.Symbol.Ticker);
                    else if (newQty > 0m)
                        _log.Debug("[SWING-DB-SKIP] Skipping MarkClosed - newQty={NewQty} > 0 sym={Sym}", newQty, req.Symbol.Ticker);
                }
            }

            // ako je real trade i gubitak -> uključi cooldown za simbol
            if (!isPaper && realizedPnl < 0m)
            {
                lock (_sync)
                {
                    _lastLossUtc[req.Symbol.Ticker] = utcNow;
                }

                _log.Information(
                    "[COOLDOWN-SET] {Sym} loss={Loss:F2} at {Utc:o}, cooldown={CdMin}min",
                    req.Symbol.Ticker, realizedPnl, utcNow, _symbolCooldownAfterLoss.TotalMinutes);
            }

            // 2) day guards - javi koliko smo zaradili/izgubili
            _dayGuards?.OnRealizedPnl(realizedPnl, utcNow);

            // 3) journal - sad imamo sve info
            // Uzmi ATR sizing metadata ako postoji
            (decimal riskFraction, decimal? atrUsed, decimal? priceRisk) sizingMeta = (0m, null, null);
            if (!string.IsNullOrWhiteSpace(req.CorrelationId))
            {
                lock (_sync)
                {
                    if (_sizingMetadata.TryGetValue(req.CorrelationId, out var meta))
                    {
                        sizingMeta = meta;
                        // Obriši nakon upotrebe (cleanup)
                        _sizingMetadata.Remove(req.CorrelationId);
                    }
                }
            }

            // broker_order_id za trade_fills / trade_journal - iz pending ordera (po correlationId)
            string? brokerOrderId = null;
            if (_orders.TryGet(req.CorrelationId ?? string.Empty, out var pending) && pending != null)
                brokerOrderId = pending.BrokerOrderId;

            var entry = new TradeJournalEntry(
                Utc: utcNow,
                Symbol: req.Symbol.Ticker,
                Side: req.Side.ToString(),
                Quantity: req.Quantity,
                Price: fillPx,
                Notional: notional,
                RealizedPnl: realizedPnl,
                IsPaper: isPaper,
                IsExit: (req.CorrelationId ?? string.Empty).StartsWith("exit-", StringComparison.OrdinalIgnoreCase),
                Strategy: _strategy.GetType().Name,
                CorrelationId: req.CorrelationId,
                BrokerOrderId: brokerOrderId,
                EstimatedFeeUsd: _fees.EstimatedPerOrderUsd,
                PlannedPrice: req.LimitPrice,
                RiskFraction: sizingMeta.riskFraction > 0m ? sizingMeta.riskFraction : null,
                AtrUsed: sizingMeta.atrUsed,
                PriceRisk: sizingMeta.priceRisk,
                Exchange: req.Symbol.Exchange
            );

            // 3a) DB: trade_journal (opciono)
            if (_journalRepo is not null)
            {
                try { _ = _journalRepo.InsertAsync(entry, CancellationToken.None); }
                catch (Exception ex) { _log.Warning(ex, "[DB-JOURNAL] insert scheduling failed"); }
            }

            // 3c) DB: trading.trade_fills (opciono)
            if (_fillRepo is not null)
            {
                try
                {
                    _ = _fillRepo.InsertAsync(
                        utc: entry.Utc,
                        symbol: entry.Symbol,
                        side: entry.Side,
                        quantity: entry.Quantity,
                        price: entry.Price,
                        notional: entry.Notional,
                        realizedPnl: entry.RealizedPnl,
                        isPaper: entry.IsPaper,
                        isExit: entry.IsExit,
                        strategy: entry.Strategy,
                        correlationId: entry.CorrelationId,
                        brokerOrderId: entry.BrokerOrderId,
                        estimatedFeeUsd: entry.EstimatedFeeUsd,
                        exchange: entry.Exchange,
                        ct: CancellationToken.None
                    );
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[DB-FILLS] insert scheduling failed");
                }
            }

            // 3d) DB: daily_pnl
            // FIX: Pozovi AddTradeAsync samo kada ima realizedPnl != 0 ILI je exit trade
            // (BUY tradeovi imaju realizedPnl = 0, pa ne treba da se poziva AddTradeAsync)
            if (_pnlRepo is not null && (realizedPnl != 0m || entry.IsExit))
            {
                _log.Information(
                    "[DB-PNL-CALL] Calling AddTradeAsync date={Date} pnl={Pnl:F2} side={Side} symbol={Sym} isPaper={Paper} isExit={Exit}",
                    utcNow.Date, realizedPnl, req.Side, req.Symbol.Ticker, isPaper, entry.IsExit);
                try 
                { 
                    _ = _pnlRepo.AddTradeAsync(utcNow, realizedPnl, CancellationToken.None)
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                _log.Error(t.Exception, "[DB-PNL-ERROR] AddTradeAsync failed date={Date} pnl={Pnl}", utcNow.Date, realizedPnl);
                            }
                            else
                            {
                                _log.Information("[DB-PNL-SUCCESS] AddTradeAsync completed date={Date} pnl={Pnl:F2}", utcNow.Date, realizedPnl);
                            }
                        });
                }
                catch (Exception ex) 
                { 
                    _log.Warning(ex, "[DB-PNL] add trade scheduling failed date={Date} pnl={Pnl}", utcNow.Date, realizedPnl); 
                }
            }
            else if (_pnlRepo is null)
            {
                _log.Warning("[DB-PNL-SKIP] _pnlRepo is NULL - skipping AddTradeAsync date={Date} pnl={Pnl:F2}", utcNow.Date, realizedPnl);
            }
            else if (realizedPnl == 0m && !entry.IsExit)
            {
                _log.Debug("[DB-PNL-SKIP] Skipping AddTradeAsync for BUY trade with realizedPnl=0 date={Date} symbol={Sym}", utcNow.Date, req.Symbol.Ticker);
            }

            // 4) log + eventualno estimated fee
            if (isPaper)
            {
                _log.Information(
                    "[PAPER-FILLED] {Side} {Sym} x{Qty:F6} @ {Px:F2} notional={Notional:F2} realized={PnL:F2}",
                    req.Side, req.Symbol.Ticker, req.Quantity, fillPx, notional, realizedPnl);
            }
            else
            {
                _log.Information(
                    "[REAL-FILLED] {Side} {Sym} x{Qty:F6} @ {Px:F2} notional={Notional:F2} realized={PnL:F2}",
                    req.Side, req.Symbol.Ticker, req.Quantity, fillPx, notional, realizedPnl);

                if (_useEstimatedCommissionOnReal)
                    ApplyEstimatedCommission(notional);
            }

            // 5) Prometheus order metrics
            try
            {
                var symbol = req.Symbol.Ticker;

                if (req.Side == OrderSide.Buy)
                    OrderMetrics.Instance.FilledBuy(symbol, (double)notional);
                else if (req.Side == OrderSide.Sell)
                    OrderMetrics.Instance.FilledSell(symbol, (double)notional);

                var posNow = _positionBook.Get(symbol);
                var qtyNow = posNow?.Quantity ?? 0m;
                OrderMetrics.Instance.SetOpenPositions(symbol, (double)qtyNow);

                if (req.LimitPrice.HasValue)
                {
                    var expected = req.LimitPrice.Value;
                    var slip = (double)(fillPx - expected);
                    OrderMetrics.Instance.SetSlippage(symbol, slip);
                }

                var latencyMs = (utcNow - req.TimestampUtc).TotalMilliseconds;
                if (latencyMs >= 0)
                    OrderMetrics.Instance.SetFillLatency(symbol, latencyMs);
            }
            catch
            {
                // metrics nikad ne sme da sruši trading engine
            }
        }
        /// <summary>
        /// Ovo zove broker adapter (IbkrOrderService) kad stigne pravi fill.
        /// </summary>
        private readonly HashSet<string> _lateFillLogged = new(); // guarded by _sync
        public void OnRealFilled(OrderRequest req, decimal fillPx, DateTime utcNow)
        {
            var corr = req.CorrelationId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(corr))
                return;

            // IbkrOrderService NAM vec salje DELTA qty po fill-u
            var sliceQty = req.Quantity;
            if (sliceQty <= 0m)
            {
                _log.Warning(
                    "[FILL-IGNORED] non-positive slice for {Sym} corr={Corr} slice={Slice:F6} px={Px:F4}",
                    req.Symbol.Ticker, corr, sliceQty, fillPx);
                return;
            }


            // NOVO: idempotentnost za real fills
            if (IsDuplicateRealFill(corr, sliceQty, fillPx, utcNow))
            {
                _log.Warning(
                    "[FILL-DUP] Ignoring duplicate real fill for {Sym} corr={Corr} slice={Slice:F6} px={Px:F4}",
                    req.Symbol.Ticker, corr, sliceQty, fillPx);
                return;
            }
            // KRAJ NOVO bloka

            decimal newCum;
            lock (_sync)
            {
                _cumFilledByCorrId.TryGetValue(corr, out var prevCum);
                newCum = prevCum + sliceQty;       // ovde gradimo CUM iz delta-ova
                _cumFilledByCorrId[corr] = newCum;
            }

            // Ako vise nemamo pending, ovo je "late fill" - samo loguj prvi put
            if (!HasPendingByCorrelation(corr))
            {
                bool first;
                lock (_sync)
                {
                    first = _lateFillLogged.Add(corr);
                }

                if (first)
                {
                    _log.Warning(
                        "[LATE-FILL] Accounting-only apply for {Sym} corr={Corr} px={Px:F4} slice={Slice:F6} cum={Cum:F6}",
                        req.Symbol.Ticker, corr, fillPx, sliceQty, newCum);
                }
            }

            // U engine accounting puštamo samo DELTA slice
            var sliceReq = new OrderRequest(
                symbol: req.Symbol,
                side: req.Side,
                type: req.Type,
                quantity: sliceQty,
                limitPrice: req.LimitPrice,
                tif: req.Tif,
                correlationId: req.CorrelationId,
                timestampUtc: utcNow,
                ocoGroupId: req.OcoGroupId,
                stopPrice: req.StopPrice,
                isExit: req.IsExit
            );

            ApplyFillCore(sliceReq, fillPx, utcNow, isPaper: false);
        }
        // =========================
        //  REAL place
        // =========================
        private async Task PlaceRealAsync(OrderRequest req)
        {
            try
            {
                using var cts = new CancellationTokenSource(_brokerPlaceTimeout);

                var brokerId = await _orderService!
                    .PlaceAsync(req)
                    .ConfigureAwait(false);

                _orders.TrySetBrokerOrderId(req.CorrelationId, brokerId);

                if (_orderRepo is not null)
                {
                    try
                    {
                        await _orderRepo.MarkSentAsync(
                            id: req.CorrelationId,
                            brokerOrderId: brokerId,
                            sentUtc: DateTime.UtcNow,
                            ct: CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[DB-ORDERS] MarkSent failed corr={Corr}", req.CorrelationId);
                    }
                }
                _log.Information("[PLACE] id={Id} {Side} {Sym} x{Qty} @ {Px}", brokerId, req.Side, req.Symbol.Ticker, req.Quantity, req.LimitPrice);
                if (_orders.TryRemove(req.CorrelationId, out var prev) && prev is not null)
                {
                    var updated = new PendingOrder(
                        Req: prev.Req,
                        ReservedUsd: prev.ReservedUsd,
                        AtUtc: prev.AtUtc,
                        BrokerOrderId: brokerId,
                        LastFeeUsd: prev.LastFeeUsd,
                        LastExecId: prev.LastExecId);
                    _orders.TryAdd(updated);
                }
            }
            catch (OperationCanceledException ocex)
            {
                _log.Warning(ocex, "[PLACE-TIMEOUT] broker did not accept order in {Sec}s for {Sym} corr={Corr}", _brokerPlaceTimeout.TotalSeconds, req.Symbol.Ticker, req.CorrelationId);

                if (_orderRepo is not null)
                {
                    try
                    {
                        await _orderRepo.UpdateStatusAsync(
                            id: req.CorrelationId,
                            status: "place-timeout",
                            lastMsg: $"timeout after {_brokerPlaceTimeout.TotalSeconds}s",
                            ct: CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[DB-ORDERS] UpdateStatus(place-timeout) failed");
                    }
                }

                RollbackPendingAndReserves(req);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[PLACE-ERROR] failed to place order for {Sym} corr={Corr}", req.Symbol.Ticker, req.CorrelationId);

                if (_orderRepo is not null)
                {
                    try
                    {
                        await _orderRepo.UpdateStatusAsync(
                            id: req.CorrelationId,
                            status: "place-error",
                            lastMsg: ex.Message,
                            ct: CancellationToken.None);
                    }
                    catch (Exception dex)
                    {
                        _log.Warning(dex, "[DB-ORDERS] UpdateStatus(place-error) failed");
                    }
                }

                RollbackPendingAndReserves(req);
            }
        }
        // =========================
        //  EXIT heuristika
        // =========================
        private void EvaluatePaperExitsOnQuote(MarketQuote q)
        {

            // === REAL MODE ===
            // Ako postoji _orderService, znači da smo povezani na pravog brokera.
            // U tom slučaju exit-e upravlja OCO (TP/SL) i NE SME da šaljemo dodatne exit naloge.
            if (_orderService != null)
            {
                TryEvaluateRealProtectTradeOnQuote(q);
                return;
            }

            try
            {
                if (q?.Symbol is null || (!q.Bid.HasValue && !q.Last.HasValue))
                    return;

                var sym = q.Symbol.Ticker;
                var pos = _positionBook.Get(sym);
                if (pos is null || pos.Quantity <= 0m)
                    return;

                var entry = pos.AveragePrice;
                if (entry <= 0m)
                    return;

                var refPx = q.Bid ?? q.Last ?? 0m;
                if (refPx <= 0m)
                    return;

                lock (_sync)
                {
                    if (_exitPending.Contains(sym))
                        return;
                }

                // -------- runtime state --------
                PositionRuntimeState? rt;
                lock (_sync)
                {
                    _posRuntime.TryGetValue(sym, out rt);
                }

                var now = DateTime.UtcNow;
                var currentPhase = TradingPhase.GetPhase(now);

                // -------- GAP DETECTION na open-u (multi-day swing) --------
                if (SwingHelpers.IsSwingMode(_swingConfig) && rt != null && currentPhase == TradingPhase.Phase.Open1H)
                {
                    // Proveri da li je ovo prvi quote u open_1h fazi (gap detection)
                    if (!rt.LastClosePrice.HasValue || !rt.LastCloseUtc.HasValue)
                    {
                        // Prvi put u open_1h - sacuvaj trenutnu cenu kao LastClosePrice za sledeci dan
                        // (ovo ce se azurirati u close fazi)
                        lock (_sync)
                        {
                            if (rt.LastClosePrice == null)
                            {
                                rt.LastClosePrice = refPx;
                                rt.LastCloseUtc = now;
                            }
                        }
                    }
                    else
                    {
                        // Proveri gap izmedju prethodnog close-a i trenutnog open-a
                        var lastClose = rt.LastClosePrice.Value;
                        var gapFrac = lastClose > 0m ? (refPx - lastClose) / lastClose : 0m;
                        var gapLossFrac = -gapFrac; // negativan gap = loss
                        
                        // Ako je gap loss prekoračen, automatski exit
                        if (gapLossFrac >= _swingConfig.MaxOvernightGapLossPct && !rt.GapExitExecuted)
                        {
                            rt.GapExitExecuted = true;
                            
                            var pnl = (refPx - rt.EntryPrice) * pos.Quantity;
                            var pnlPct = rt.EntryPrice > 0m ? ((refPx - rt.EntryPrice) / rt.EntryPrice) * 100m : 0m;
                            
                            _log.Warning(
                                "[SWING-GAP-OPEN-EXIT] {Sym} GAP DETECTED ON OPEN gap={GapPct:P2} lastClose={LastClose:F2} open={Open:F2} entry={Entry:F2} pnl={Pnl:F2} {PnlPct:+#0.00;-#0.00}%",
                                sym,
                                gapLossFrac,
                                lastClose,
                                refPx,
                                rt.EntryPrice,
                                pnl,
                                pnlPct);
                            
                            try { StrategyMetrics.Instance.GapDetectedOnOpen(sym, (double)gapLossFrac * 100.0, (double)pnlPct); } catch { }
                            
                            SendExit(
                                sym,
                                pos.Quantity,
                                refPx,
                                $"GAP-OPEN-EXIT gap={gapLossFrac:P2} lastClose={lastClose:F2} open={refPx:F2} entry={rt.EntryPrice:F2}"
                            );
                            return; // Izlazimo jer smo vec pozvali SendExit
                        }
                        else if (gapLossFrac > 0.01m) // Gap > 1% (bilo loss ili profit)
                        {
                            // Loguj gap (ali ne exit ako nije prekoračen)
                            _log.Information(
                                "[SWING-GAP-DETECTED] {Sym} gap={GapPct:P2} lastClose={LastClose:F2} open={Open:F2} (within threshold)",
                                sym,
                                gapFrac,
                                lastClose,
                                refPx);
                        }
                    }
                }
                
                // Azuriraj LastClosePrice u close fazi (za gap detection sledeci dan)
                if (SwingHelpers.IsSwingMode(_swingConfig) && rt != null && currentPhase == TradingPhase.Phase.Close)
                {
                    lock (_sync)
                    {
                        rt.LastClosePrice = refPx;
                        rt.LastCloseUtc = now;
                    }
                }

                if (rt is null)
                {
                    // Determine regime and symbol baseline if not set (fallback)
                    string? regime = null;
                    string? symbolBaseline = null;
                    decimal? atrAtEntry = null;
                    
                    lock (_sync)
                    {
                        if (_atr.TryGetValue(sym, out var atrState) && atrState.Atr.HasValue && entry > 0m)
                        {
                            var rawAtr = atrState.Atr.Value;
                            var minAtrFrac = _limits.MinAtrFraction;
                            var minAtrAbs = entry * minAtrFrac;
                            var flooredAtr = Math.Max(rawAtr, minAtrAbs);
                            atrAtEntry = flooredAtr;
                            
                            var atrFrac = flooredAtr / entry;
                            if (atrFrac > 0.0005m) regime = "HIGH";
                            else if (atrFrac > 0.00015m) regime = "NORMAL";
                            else regime = "LOW";
                            
                            if (atrFrac < 0.0002m) symbolBaseline = "slow";
                            else if (atrFrac <= 0.0005m) symbolBaseline = "normal";
                            else symbolBaseline = "fast";
                        }
                    }
                    
                    rt = new PositionRuntimeState
                    {
                        EntryUtc = now,
                        EntryPrice = entry,
                        BestPrice = refPx,
                        IsExternal = false,
                        RegimeAtEntry = regime ?? "LOW",
                        SymbolBaseline = symbolBaseline ?? "normal",
                        AtrAtEntry = atrAtEntry
                    };
                    lock (_sync) _posRuntime[sym] = rt;
                }
                else
                {
                    if (refPx > rt.BestPrice)
                        rt.BestPrice = refPx;
                }

                if (TryHandleProtectTradeOnQuote(sym, pos.Quantity, entry, refPx, rt, now, isRealMode: false))
                    return;

                // -------- 1) TIME EXIT (regime/symbol aware) --------
                var holding = now - rt.EntryUtc;
                var regimeAtEntry = rt.RegimeAtEntry ?? "LOW";
                var symbolBaselineAtEntry = rt.SymbolBaseline ?? "normal";

                if (SwingHelpers.IsSwingMode(_swingConfig))
                {
                    // Swing: koristimo MaxHoldingDays iz configa sa regime/symbol adjustments
                    if (_swingConfig.MaxHoldingDays > 0)
                    {
                        var baseMaxDays = _swingConfig.MaxHoldingDays;
                        
                        // Per-regime adjustment: LOW - kraci, HIGH - duzi
                        var regimeMultiplier = regimeAtEntry switch
                        {
                            "LOW" => 0.8m,      // 20% kraci
                            "HIGH" => 1.2m,     // 20% duži
                            _ => 1.0m           // NORMAL: bez promene
                        };
                        
                        // Per-symbol baseline adjustment: slow - duzi, fast - kraci
                        var baselineMultiplier = symbolBaselineAtEntry switch
                        {
                            "slow" => 1.15m,    // 15% duži
                            "fast" => 0.85m,     // 15% kraci
                            _ => 1.0m            // normal: bez promene
                        };
                        
                        var adjustedMaxDays = baseMaxDays * regimeMultiplier * baselineMultiplier;
                        var maxAge = TimeSpan.FromDays((double)adjustedMaxDays);
                        
                        if (holding >= maxAge)
                        {
                            var pnl = (refPx - entry) * pos.Quantity;
                            var pnlPct = entry > 0m ? ((refPx - entry) / entry) * 100m : 0m;
                            
                            SendExit(
                                sym,
                                pos.Quantity,
                                refPx,
                                $"TIME EXIT SWING {holding.TotalDays:F1}d @ {refPx:F2} (entry {entry:F2}, baseMaxDays={baseMaxDays}, adjusted={adjustedMaxDays:F1}, regime={regimeAtEntry}, baseline={symbolBaselineAtEntry}, pnl={pnl:F2} {pnlPct:+#0.00;-#0.00}%)"
                            );
                            
                            // Outcome logging
                            try { StrategyMetrics.Instance.TimeExitOccurred("Swing", sym, regimeAtEntry, symbolBaselineAtEntry, (double)pnlPct, holding.TotalDays); } catch { }
                            
                            return;
                        }
                    }
                }
                else
                {
                    // Intraday / Off: regime/symbol aware time exit
                    var baseMaxHold = _maxHoldTime;
                    
                    // Per-regime adjustment: LOW - kraci, HIGH - duzi
                    var regimeMultiplier = regimeAtEntry switch
                    {
                        "LOW" => 0.75m,         // 25% kraci (agresivniji exit)
                        "HIGH" => 1.25m,         // 25% duži
                        _ => 1.0m                // NORMAL: bez promene
                    };
                    
                    // Per-symbol baseline adjustment
                    var baselineMultiplier = symbolBaselineAtEntry switch
                    {
                        "slow" => 1.2m,          // 20% duži
                        "fast" => 0.8m,           // 20% kraci
                        _ => 1.0m                 // normal: bez promene
                    };
                    
                    var adjustedMaxHold = TimeSpan.FromMilliseconds(baseMaxHold.TotalMilliseconds * (double)(regimeMultiplier * baselineMultiplier));
                    
                    if (holding >= adjustedMaxHold)
                    {
                        var pnl = (refPx - entry) * pos.Quantity;
                        var pnlPct = entry > 0m ? ((refPx - entry) / entry) * 100m : 0m;
                        
                        SendExit(
                            sym,
                            pos.Quantity,
                            refPx,
                            $"TIME EXIT INTRADAY {holding.TotalMinutes:F1}min @ {refPx:F2} (entry {entry:F2}, base={baseMaxHold.TotalMinutes:F1}min, adjusted={adjustedMaxHold.TotalMinutes:F1}min, regime={regimeAtEntry}, baseline={symbolBaselineAtEntry}, pnl={pnl:F2} {pnlPct:+#0.00;-#0.00}%)"
                        );
                        
                        // Outcome logging
                        try { StrategyMetrics.Instance.TimeExitOccurred("Intraday", sym, regimeAtEntry, symbolBaselineAtEntry, (double)pnlPct, holding.TotalMinutes / 60.0); } catch { }
                        
                        return;
                    }
                }

                // -------- ATR lookup sa floor-om na % cene --------
                decimal? atrOpt = null;
                lock (_sync)
                {
                    if (_atr.TryGetValue(sym, out var s) && s.Atr.HasValue && s.Atr.Value > 0m)
                    {
                        var rawAtr = s.Atr.Value;

                        // minimalni ATR kao % od cene (iz config-a)
                        var minAtrFrac = _limits.MinAtrFraction;
                        var minAtrAbs = entry * minAtrFrac;

                        var flooredAtr = Math.Max(rawAtr, minAtrAbs);
                        atrOpt = flooredAtr;
                    }
                }

                // -------- 2) TP/SL --------
                decimal tpLevel, slLevel;
                if (atrOpt.HasValue)
                {
                    var atr = atrOpt.Value;

                    // ATR distance
                    var atrTpDist = TpAtrMultiple * atr;
                    var atrSlDist = SlAtrMultiple * atr;

                    // Minimalna procentualna distanca (fallback)
                    var pctTpDist = entry * _tpFraction;   // npr. 2%
                    var pctSlDist = entry * _slFraction;   // npr. 1%

                    // Uvek uzmi VECU distancu: ili ATR ili % od cene
                    var tpDist = Math.Max(atrTpDist, pctTpDist);
                    var slDist = Math.Max(atrSlDist, pctSlDist);

                    tpLevel = entry + tpDist;
                    slLevel = entry - slDist;
                }
                else
                {
                    // Kad nemamo ATR, koristimo samo % od cene
                    var tpDist = entry * _tpFraction;

                    tpLevel = entry + tpDist;
                    slLevel = entry * (1m - _slFraction);
                }


                var hitTp = refPx >= tpLevel;
                var hitSl = refPx <= slLevel;

                // -------- 3) TRAILING --------
                bool hitTrail = false;
                string? trailInfo = null;
                decimal? trailStop = null;
                decimal? atrUsed = atrOpt;

                if (atrOpt.HasValue)
                {
                    var atr = atrOpt.Value;
                    var activationPrice = entry + TrailActivateAtrMultiple * atr;

                    if (rt.BestPrice >= activationPrice)
                    {
                        // Trailing je aktiviran - proveri da li je prvi put
                        if (!rt.TrailingArmed)
                        {
                            rt.TrailingArmed = true;
                            _log.Information(
                                "[TRAIL-ARMED] {Sym} entry={Entry:F2} best={Best:F2} activation={Act:F2} atr={Atr:F4}",
                                sym, entry, rt.BestPrice, activationPrice, atr);
                            
                            try { StrategyMetrics.Instance.TrailingArmed(sym, "ATR", (double)atr); } catch { }
                        }

                        trailStop = rt.BestPrice - TrailDistanceAtrMultiple * atr;

                        // Proveri da li se trail stop pomera naviše (update)
                        var shouldUpdate = !rt.LastTrailStop.HasValue || trailStop.Value > rt.LastTrailStop.Value;
                        var canUpdate = !rt.LastTrailUpdateUtc.HasValue || 
                                       (now - rt.LastTrailUpdateUtc.Value) >= TimeSpan.FromSeconds(1); // Rate-limit: max 1 update po sekundi

                        if (shouldUpdate && canUpdate)
                        {
                            rt.LastTrailStop = trailStop.Value;
                            rt.LastTrailUpdateUtc = now;
                            
                            _log.Information(
                                "[TRAIL-UPDATE] {Sym} best={Best:F2} stop={Stop:F2} entry={Entry:F2} atr={Atr:F4}",
                                sym, rt.BestPrice, trailStop.Value, entry, atr);
                            
                            try { StrategyMetrics.Instance.TrailingUpdate(sym, "ATR", (double)atr, (double)trailStop.Value); } catch { }
                        }

                        // Proveri da li je cena pala ispod trail stop-a (fire)
                        if (refPx <= trailStop.Value)
                        {
                            hitTrail = true;
                            
                            // Agresivniji limit za trailing exit: koristi bid - slippage ako je bid dostupan
                            // Ako nema bid, koristi refPx (fallback)
                            decimal exitLimitPrice = refPx;
                            if (q.Bid.HasValue && q.Bid.Value > 0m)
                            {
                                // Agresivni limit: bid - 1 tick (0.01) za brži fill
                                exitLimitPrice = Math.Max(q.Bid.Value - 0.01m, trailStop.Value);
                            }
                            
                            trailInfo = $"TRAIL-FIRE-ATR best={rt.BestPrice:F2} stop={trailStop.Value:F2} now={refPx:F2} limit={exitLimitPrice:F2} entry={entry:F2} atr={atr:F4}";
                            
                            _log.Information(
                                "[TRAIL-FIRE] {Sym} best={Best:F2} stop={Stop:F2} now={Now:F2} limit={Limit:F2} entry={Entry:F2} atr={Atr:F4}",
                                sym, rt.BestPrice, trailStop.Value, refPx, exitLimitPrice, entry, atr);
                            
                            try { StrategyMetrics.Instance.TrailingFire(sym, "ATR", (double)atr, (double)refPx, (double)entry); } catch { }
                            
                            // Koristi agresivniji limit za trailing exit
                            SendExit(sym, pos.Quantity, exitLimitPrice, trailInfo);
                            return; // Izlazimo jer smo vec pozvali SendExit
                        }
                    }
                }
                else
                {
                    var activationLevel = entry * (1m + _trailActivateFraction);

                    if (rt.BestPrice >= activationLevel)
                    {
                        // Trailing je aktiviran - proveri da li je prvi put
                        if (!rt.TrailingArmed)
                        {
                            rt.TrailingArmed = true;
                            _log.Information(
                                "[TRAIL-ARMED] {Sym} entry={Entry:F2} best={Best:F2} activation={Act:F2} pct={Pct:P2}",
                                sym, entry, rt.BestPrice, activationLevel, _trailActivateFraction);
                            
                            try { StrategyMetrics.Instance.TrailingArmed(sym, "PCT", 0.0); } catch { }
                        }

                        trailStop = rt.BestPrice * (1m - _trailDistanceFraction);

                        // Proveri da li se trail stop pomera naviše (update)
                        var shouldUpdate = !rt.LastTrailStop.HasValue || trailStop.Value > rt.LastTrailStop.Value;
                        var canUpdate = !rt.LastTrailUpdateUtc.HasValue || 
                                       (now - rt.LastTrailUpdateUtc.Value) >= TimeSpan.FromSeconds(1); // Rate-limit: max 1 update po sekundi

                        if (shouldUpdate && canUpdate)
                        {
                            rt.LastTrailStop = trailStop.Value;
                            rt.LastTrailUpdateUtc = now;
                            
                            _log.Information(
                                "[TRAIL-UPDATE] {Sym} best={Best:F2} stop={Stop:F2} entry={Entry:F2} pct={Pct:P2}",
                                sym, rt.BestPrice, trailStop.Value, entry, _trailDistanceFraction);
                            
                            try { StrategyMetrics.Instance.TrailingUpdate(sym, "PCT", 0.0, (double)trailStop.Value); } catch { }
                        }

                        // Proveri da li je cena pala ispod trail stop-a (fire)
                        if (refPx <= trailStop.Value)
                        {
                            hitTrail = true;
                            
                            // Agresivniji limit za trailing exit: koristi bid - slippage ako je bid dostupan
                            // Ako nema bid, koristi refPx (fallback)
                            decimal exitLimitPrice = refPx;
                            if (q.Bid.HasValue && q.Bid.Value > 0m)
                            {
                                // Agresivni limit: bid - 1 tick (0.01) za brži fill
                                exitLimitPrice = Math.Max(q.Bid.Value - 0.01m, trailStop.Value);
                            }
                            
                            trailInfo = $"TRAIL-FIRE-PCT best={rt.BestPrice:F2} stop={trailStop.Value:F2} now={refPx:F2} limit={exitLimitPrice:F2} entry={entry:F2}";
                            
                            _log.Information(
                                "[TRAIL-FIRE] {Sym} best={Best:F2} stop={Stop:F2} now={Now:F2} limit={Limit:F2} entry={Entry:F2} pct={Pct:P2}",
                                sym, rt.BestPrice, trailStop.Value, refPx, exitLimitPrice, entry, _trailDistanceFraction);
                            
                            try { StrategyMetrics.Instance.TrailingFire(sym, "PCT", 0.0, (double)refPx, (double)entry); } catch { }
                            
                            // Koristi agresivniji limit za trailing exit
                            SendExit(sym, pos.Quantity, exitLimitPrice, trailInfo);
                            return; // Izlazimo jer smo vec pozvali SendExit
                        }
                    }
                }

                // Trailing exit je vec obradjen (pozvao SendExit i return-ovao)
                if (hitTrail)
                    return;
                
                if (!hitTp && !hitSl)
                    return;
                
                // -------- reason message --------
                string reason;
                if (hitTp)
                {
                    reason = atrOpt.HasValue
                        ? $"TP-ATR now={refPx:F2} entry={entry:F2} tp={tpLevel:F2} atr={atrOpt.Value:F4}"
                        : $"TP-PCT {refPx:F2} (entry {entry:F2}, +{_tpFraction:P2})";
                }
                else
                {
                    reason = atrOpt.HasValue
                        ? $"SL-ATR now={refPx:F2} entry={entry:F2} sl={slLevel:F2} atr={atrOpt.Value:F4}"
                        : $"SL-PCT {refPx:F2} (entry {entry:F2}, -{_slFraction:P2})";
                }
                
                SendExit(sym, pos.Quantity, refPx, reason);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[ERROR] exit-eval failed");
            }
        }

        private void TryEvaluateRealProtectTradeOnQuote(MarketQuote q)
        {
            if (!_settings.ProtectTrade.Enabled)
                return;

            if (_orderService is null)
                return;

            if (_swingConfig is null || _swingConfig.Mode != SwingMode.Swing || !_swingConfig.AutoExitReal)
                return;

            try
            {
                if (q?.Symbol is null || (!q.Bid.HasValue && !q.Last.HasValue))
                    return;

                var sym = q.Symbol.Ticker;
                var pos = _positionBook.Get(sym);
                if (pos is null || pos.Quantity <= 0m)
                    return;

                var entry = pos.AveragePrice;
                if (entry <= 0m)
                    return;

                if (IsExternalPosition(sym))
                    return;

                var refPx = q.Bid ?? q.Last ?? 0m;
                if (refPx <= 0m)
                    return;

                lock (_sync)
                {
                    if (_exitPending.Contains(sym))
                        return;
                }

                var now = DateTime.UtcNow;

                PositionRuntimeState? rt;
                lock (_sync)
                {
                    _posRuntime.TryGetValue(sym, out rt);

                    if (rt is null)
                    {
                        rt = new PositionRuntimeState
                        {
                            EntryUtc = now,
                            EntryPrice = entry,
                            BestPrice = refPx,
                            IsExternal = false
                        };
                        _posRuntime[sym] = rt;
                    }
                    else if (refPx > rt.BestPrice)
                    {
                        rt.BestPrice = refPx;
                    }
                }

                TryHandleProtectTradeOnQuote(sym, pos.Quantity, entry, refPx, rt, now, isRealMode: true);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[PROTECT-TRADE-REAL] evaluation failed");
            }
        }

        private bool TryHandleProtectTradeOnQuote(
            string symbol,
            decimal quantity,
            decimal entry,
            decimal refPx,
            PositionRuntimeState rt,
            DateTime nowUtc,
            bool isRealMode)
        {
            if (!_settings.ProtectTrade.Enabled)
                return false;

            if (quantity <= 0m || entry <= 0m || refPx <= 0m)
                return false;

            var armProfitPct = _settings.ProtectTrade.ArmProfitPct;
            if (armProfitPct <= 0m)
                return false;

            var armPrice = entry * (1m + armProfitPct);
            var protectStop = entry * (1m + _settings.ProtectTrade.StopOffsetPct);

            if (!rt.ProtectTradeArmed && rt.BestPrice >= armPrice)
            {
                rt.ProtectTradeArmed = true;
                rt.ProtectTradeArmedUtc = nowUtc;
                rt.ProtectTradeStop = protectStop;

                _log.Information(
                    "[PROTECT-TRADE-ARMED] {Sym} mode={Mode} entry={Entry:F2} best={Best:F2} trigger={Trigger:F2} stop={Stop:F2}",
                    symbol,
                    isRealMode ? "REAL" : "PAPER",
                    entry,
                    rt.BestPrice,
                    armPrice,
                    protectStop);
            }

            if (!rt.ProtectTradeArmed || !rt.ProtectTradeStop.HasValue)
                return false;

            if (refPx > rt.ProtectTradeStop.Value)
                return false;

            var reason =
                $"PROTECT-TRADE now={refPx:F2} entry={entry:F2} best={rt.BestPrice:F2} trigger={armPrice:F2} stop={rt.ProtectTradeStop.Value:F2}";

            if (isRealMode)
            {
                _log.Warning(
                    "[PROTECT-TRADE-FIRE] {Sym} REAL qty={Qty:F4} now={Now:F2} stop={Stop:F2} best={Best:F2}",
                    symbol,
                    quantity,
                    refPx,
                    rt.ProtectTradeStop.Value,
                    rt.BestPrice);

                CancelAllExitsForSymbol(symbol, nowUtc, reason);
                SendExit(symbol, quantity, refPx, reason, "exit-swing-protect");
            }
            else
            {
                _log.Warning(
                    "[PROTECT-TRADE-FIRE] {Sym} PAPER qty={Qty:F4} now={Now:F2} stop={Stop:F2} best={Best:F2}",
                    symbol,
                    quantity,
                    refPx,
                    rt.ProtectTradeStop.Value,
                    rt.BestPrice);

                SendExit(symbol, quantity, refPx, reason, "exit-swing-protect");
            }

            return true;
        }

        // =========================
        //  HEARTBEAT
        // =========================
        public void StartHeartbeat(TimeSpan period, CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // T+1 dnevni roll pre svakog snapshot-a
                        _cashService.DailyRoll(DateTime.UtcNow);

                        var cash = _lastCash;

                        // sve pozicije
                        var positions = _positionBook.Snapshot();

                        // pokaži samo žive
                        var live = positions.Where(p => p.Quantity != 0m).ToArray();
                        var posCount = live.Length;

                       

                        var posText = posCount == 0
                            ? "[none]"
                            : string.Join(", ", live.Select(p => $"{p.Symbol}:{p.Quantity:F4}@{p.AveragePrice:F2}"));

                        var exposureText = _exposure is not null ? _exposure.ToString() : "N/A";

                        var (equityApprox, inPosApprox) = GetApproxEquity();
                        var swingMode = _swingConfig.Mode.ToString();

                        _log.Information(
                            "[HB] cash Free={Free:F2} Settling={Sett:F2} InPos~{InPos:F2} equity~{Eq:F2} positions={Cnt} {Pos} exposure={Exp}, Reserved={Reserved:F2}, swingMode={SwingMode}",
                            cash.Free,
                            cash.Settling,
                            inPosApprox,
                            equityApprox,
                            posCount,
                            posText,
                            exposureText,
                            cash.Reserved,
                            swingMode);

                        // --- SWING monitoring (samo log, nema auto-sell) ---
                        if (_swingConfig is not null && SwingHelpers.IsSwingMode(_swingConfig))
                        {
                            var nowUtc = DateTime.UtcNow;

                            try
                            {

                                // NOVO - real auto-exit (ako je ukljucen u configu)
                                await EvaluateSwingAutoExitsAsync(nowUtc);

                                // IBKR EOD skim (V1 rollout: dry-run first)
                                await EvaluateIbkrEodSkimAsync(nowUtc);

                                // 1) MaxHoldingDays warning
                                if (_swingConfig.MaxHoldingDays > 0)
                                {
                                    var maxAge = TimeSpan.FromDays(_swingConfig.MaxHoldingDays);
                                    List<(string Sym, TimeSpan Age)> tooOld = new();

                                    lock (_sync)
                                    {
                                        foreach (var kvp in _posRuntime)
                                        {
                                            var age = nowUtc - kvp.Value.EntryUtc;
                                            if (age >= maxAge)
                                                tooOld.Add((kvp.Key, age));
                                        }
                                    }

                                    foreach (var item in tooOld)
                                    {
                                        _log.Warning(
                                            "[SWING-AGE] {Sym} holding {Days:F1} days >= MaxHoldingDays={MaxDays}",
                                            item.Sym,
                                            item.Age.TotalDays,
                                            _swingConfig.MaxHoldingDays);
                                    }
                                }

                                // 2) Petak + WeekendCutoff + otvorene pozicije


                                // 2) Weekend zaštita preko helpera
                                if (SwingHelpers.ShouldProtectWeekend(nowUtc, _swingConfig) && live.Length > 0)
                                {
                                    var syms = string.Join(", ", live.Select(p => p.Symbol));
                                    _log.Warning(
                                        "[SWING-WEEKEND] Weekend cutoff passed ({Cutoff}) with open positions: {Syms}",
                                        _swingConfig.WeekendCutoffUtc,
                                        syms);
                                }

                                // 3) Overnight / gap loss monitoring
                                if (live.Length > 0)
                                {
                                    foreach (var p in live)
                                    {
                                        PositionRuntimeState? rt;
                                        lock (_sync)
                                        {
                                            _posRuntime.TryGetValue(p.Symbol, out rt);
                                        }

                                        if (rt is null)
                                            continue;

                                        var age = nowUtc - rt.EntryUtc;
                                        var ageDays = age.TotalDays;

                                        // 3.1) AllowOvernight = false, a držimo više od 1 dana
                                        if (!_swingConfig.AllowOvernight && ageDays >= 1.0)
                                        {
                                            _log.Warning(
                                                "[SWING-OVERNIGHT-DISALLOWED] {Sym} holding {Days:F2} days while AllowOvernight=false",
                                                p.Symbol,
                                                ageDays);
                                        }

                                        // 3.2) MaxOvernightGapLossPct - gap protection sa auto-exit-om
                                        if (_swingConfig.MaxOvernightGapLossPct > 0m && ageDays >= 1.0 && !rt.GapExitExecuted)
                                        {
                                            if (TryGetQuote(p.Symbol, nowUtc, out var q2, out _))
                                            {
                                                var refPx = q2.Bid ?? q2.Last ?? q2.Ask ?? 0m;
                                                if (refPx > 0m && rt.EntryPrice > 0m)
                                                {
                                                    var lossFrac = (refPx - rt.EntryPrice) / rt.EntryPrice;

                                                    if (lossFrac <= -_swingConfig.MaxOvernightGapLossPct)
                                                    {
                                                        // Gap loss prekoračen - automatski exit
                                                        rt.GapExitExecuted = true;
                                                        
                                                        var pnl = (refPx - rt.EntryPrice) * p.Quantity;
                                                        var pnlPct = rt.EntryPrice > 0m ? ((refPx - rt.EntryPrice) / rt.EntryPrice) * 100m : 0m;
                                                        
                                                        _log.Warning(
                                                            "[SWING-GAP-EXIT] {Sym} GAP LOSS EXIT loss={LossPct:P2} age={Days:F2}d entry={Entry:F2} now={NowPx:F2} maxLoss={MaxLoss:P2} pnl={Pnl:F2} {PnlPct:+#0.00;-#0.00}%",
                                                            p.Symbol,
                                                            lossFrac,
                                                            ageDays,
                                                            rt.EntryPrice,
                                                            refPx,
                                                            _swingConfig.MaxOvernightGapLossPct,
                                                            pnl,
                                                            pnlPct);
                                                        
                                                        try { StrategyMetrics.Instance.GapExitExecuted(p.Symbol, (double)lossFrac * 100.0, (double)ageDays, (double)pnlPct); } catch { }
                                                        
                                                        SendExit(
                                                            p.Symbol,
                                                            p.Quantity,
                                                            refPx,
                                                            $"GAP-EXIT loss={lossFrac:P2} age={ageDays:F2}d entry={rt.EntryPrice:F2} now={refPx:F2} maxLoss={_swingConfig.MaxOvernightGapLossPct:P2}"
                                                        );
                                                    }
                                                    else
                                                    {
                                                        // Samo loguj ako je loss blizu praga (ali ne prekoračen)
                                                        if (lossFrac <= -_swingConfig.MaxOvernightGapLossPct * 0.8m) // 80% od praga
                                                        {
                                                            _log.Information(
                                                                "[SWING-OVERNIGHT-LOSS-WARNING] {Sym} loss={LossPct:P2} age={Days:F2}d entry={Entry:F2} now={NowPx:F2} maxLoss={MaxLoss:P2} (approaching threshold)",
                                                            p.Symbol,
                                                            lossFrac,
                                                            ageDays,
                                                            rt.EntryPrice,
                                                            refPx,
                                                            _swingConfig.MaxOvernightGapLossPct);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "[SWING] heartbeat swing checks failed");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[HB] failed");
                    }

                    try
                    {
                        await Task.Delay(period, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }, ct);
        }
        // =========================
        //  PENDING EXPIRY
        // =========================
        public void StartPendingExpiryWatcher(TimeSpan sweepEvery, TimeSpan ttl, CancellationToken ct)
        {
            var exitUnsentTtl = TimeSpan.FromSeconds(30);

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var now = DateTime.UtcNow;
                        var pendings = _orders.Snapshot();

                        foreach (var po in pendings)
                        {
                            var isExit =
                                po.Req.IsExit ||
                                po.CorrelationId.StartsWith("exit-", StringComparison.OrdinalIgnoreCase);

                            var age = now - po.AtUtc;

                            // EXIT: ako je poslato brokeru, NIKAD ne TTL-cancel (ostaje zaštita aktivna)
                            if (isExit && _orderService is not null && !string.IsNullOrEmpty(po.BrokerOrderId))
                                continue;

                            // EXIT: ako NIJE poslato brokeru (nema brokerId), očisti brzo da ne ostane exitPending zaglavljen
                            if (isExit && string.IsNullOrEmpty(po.BrokerOrderId) && age >= exitUnsentTtl)
                            {
                                if (_orders.TryRemove(po.CorrelationId, out var removed) && removed is not null)
                                {
                                    if (removed.ReservedUsd > 0m)
                                        _cashService.Unreserve(removed.ReservedUsd);

                                    lock (_sync) _exitPending.Remove(removed.Req.Symbol.Ticker);
                                    ClearCumFilled(removed.CorrelationId);

                                    _log.Warning("[PENDING-EXPIRE] removed EXIT local-only corr={Corr} sym={Sym} age={Age}s",
                                        removed.CorrelationId, removed.Req.Symbol.Ticker, age.TotalSeconds);

                                    if (_orderRepo is not null)
                                        _ = _orderRepo.MarkExpiredAsync(removed.Req.CorrelationId, now, CancellationToken.None);
                                }
                                continue;
                            }

                            // ENTRY / ostalo: standard TTL
                            if (age < ttl)
                                continue;

                            if (_orderService is not null && !string.IsNullOrEmpty(po.BrokerOrderId))
                            {
                                if (TryMarkCancelRequested(po.CorrelationId, now))
                                {
                                    _log.Warning("[PENDING-EXPIRE] cancel-request corr={Corr} sym={Sym} brokerId={Bid} age={Age}s",
                                        po.CorrelationId, po.Req.Symbol.Ticker, po.BrokerOrderId, age.TotalSeconds);

                                    FireAndForgetCancel(po.BrokerOrderId, po.Req.Symbol.Ticker);

                                    if (_orderRepo is not null)
                                        _ = _orderRepo.UpdateStatusAsync(po.CorrelationId, "cancel-requested", $"ttl {ttl.TotalSeconds}s",forCrypto:false, CancellationToken.None);
                                }

                                continue;
                            }

                            if (_orders.TryRemove(po.CorrelationId, out var removed2) && removed2 is not null)
                            {
                                if (removed2.ReservedUsd > 0m)
                                    _cashService.Unreserve(removed2.ReservedUsd);

                                RollbackDayGuardCountIfUnfilledEntry(removed2, now, "pending-expire-local");
                                ClearCumFilled(removed2.CorrelationId);

                                _log.Warning("[PENDING-EXPIRE] removed local-only corr={Corr} sym={Sym} age={Age}s",
                                    removed2.CorrelationId, removed2.Req.Symbol.Ticker, age.TotalSeconds);

                                if (_orderRepo is not null)
                                    _ = _orderRepo.MarkExpiredAsync(removed2.Req.CorrelationId, now, CancellationToken.None);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[PENDING-EXPIRE] sweep failed");
                    }

                    try { await Task.Delay(sweepEvery, ct).ConfigureAwait(false); } catch { }
                }
            }, ct);
        }
        // =========================
        //  helper-i
        // =========================
        private bool HasPendingForSymbol(string ticker)
        {
            var pendings = _orders.Snapshot();
            var now = DateTime.UtcNow;

            foreach (var p in pendings)
            {
                if (p?.Req is null) 
                    continue;

                if (!string.Equals(p.Req.Symbol.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
                    continue;

                // EXIT nalozi (TP/SL, TIME EXIT) više ne blokiraju ENTRY signale
                if (p.Req.CorrelationId.StartsWith("exit-", StringComparison.OrdinalIgnoreCase))
                    continue;

                // (opciono) možeš da loguješ malo više detalja ovde ako želiš
                 var age = (now - p.AtUtc).TotalSeconds;
                 _log.Information("[PENDING-GUARD] {Sym} pending entry corr={Corr} age={Age:F1}s", ticker, p.CorrelationId, age); //mozda resis da vratis na debug !!!!log

                return true;
            }

            return false;
        }
        public void SetOrderService(IOrderService orderService)
        {
            if (_orderService is not null)
                throw new InvalidOperationException("Order service already set.");

            _orderService = orderService ?? throw new ArgumentNullException(nameof(orderService));
            _orderService.OrderUpdated += OnOrderUpdated;
            _log.Information("Order service set to {Service}", orderService.GetType().Name);
        }
        public void StartExternalPositionsSync(TimeSpan period, CancellationToken ct)
        {
            if (_externalPositions is null)
                return; // nemaš spoljne pozicije, nema sync

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await ImportExternalPositionsNowAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // normalno gašenje
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[EXT-POS-IMPORT] periodic sync failed");
                    }

                    try
                    {
                        await Task.Delay(period, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }, ct);
        }
        public async Task ImportExternalPositionsNowAsync(CancellationToken ct = default)
        {
            if (_externalPositions is null)
            {
                _log.Warning("[EXT-POS-IMPORT] No external positions provider (null)");
                return;
            }

            _log.Debug("[EXT-POS-IMPORT] Starting external position sync");
            try
            {
                var ext = await _externalPositions.GetOpenPositionsAsync(ct).ConfigureAwait(false);
                if (ext.Count == 0)
                {
                    _log.Information("[EXT-POS-IMPORT] IBKR returned 0 positions");
                }
                else
                {
                 
                    foreach (var p in ext)
                    {
                        _log.Debug("[EXT-POS] {Sym} qty={Qty} avg={Avg}", p.Symbol, p.Quantity, p.AveragePrice);
                    }
                   
                }

                // OVDE JE BITNO: mapiraš DTO -> tuple koji SyncExternalPositions očekuje
                SyncExternalPositions(ext.Select(p => (p.Symbol, p.Quantity, p.AveragePrice))
                );
                _log.Information("[EXT-POS-IMPORT] Completed");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[EXT-POS-IMPORT] Failed");
            }
        }
        private void OnOrderUpdated(OrderResult res)
        {
            if (res is null) return;

            var now = DateTime.UtcNow;

            try
            {
                var status = res.Status ?? string.Empty;
                var brokerId = res.BrokerOrderId;

                void SyncDbStatusByBrokerId(string statusToSet, string? lastMsg = null)
                {
                    if (_orderRepo == null || string.IsNullOrWhiteSpace(brokerId))
                        return;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _orderRepo.UpdateStatusByBrokerOrderIdAsync(
                                brokerOrderId: brokerId,
                                status: statusToSet,
                                lastMsg: lastMsg,
                                ct: CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning(ex,
                                "[DB-ORDERS] UpdateStatusByBrokerOrderId failed brokerId={Bid} -> {Status}",
                                brokerId, statusToSet);
                        }
                    });
                }
                
                // DEBUG: Log all OrderUpdated calls to trace commission events
                if (res.CommissionAndFees.HasValue && res.CommissionAndFees.Value > 0m)
                {
                    _log.Information("[ORD-UPD-COMM] OnOrderUpdated called with COMMISSION status={Status} brokerId={Bid} commission={Comm:F4}",
                        status, brokerId, res.CommissionAndFees.Value);
                }
                else
                {
                    _log.Debug("[ORD-UPD-DEBUG] OnOrderUpdated called status={Status} brokerId={Bid} commission={Comm}",
                        status, brokerId, res.CommissionAndFees ?? 0m);
                }

                // Jedan lookup pending-a po brokerId (da ne enumerišemo Snapshot stalno)
                // NOTE: Lookup before duplicate check so commission events can access it
                PendingOrder? poByBid = null;
                if (!string.IsNullOrWhiteSpace(brokerId))
                {
                    poByBid = _orders.Snapshot().FirstOrDefault(x => x.BrokerOrderId == brokerId);
                }

                // ============================================================
                // 0) COMMISSION EVENT (IBKR šalje zasebno) - MUST BE BEFORE DUPLICATE CHECK
                // Commission events are critical and must always be processed
                // ============================================================
                if (res.CommissionAndFees is { } fee && fee > 0m)
                {
                    _cashService.OnCommissionPaid(fee);
                    _dayGuards?.OnRealizedPnl(-fee, now);

                    var pendingOrderInfo = poByBid != null 
                        ? $"found sym={poByBid.Req?.Symbol.Ticker ?? "n/a"} corr={poByBid.CorrelationId}"
                        : "NOT FOUND";
                    
                    _log.Information(
                        "[COMMISSION] fee={Fee:F2} brokerId={Bid} status={Status} msg={Msg} pending={Pending}",
                        fee, brokerId, status, res.Message ?? "n/a", pendingOrderInfo);

                    if (_pnlRepo != null)
                    {
                        _log.Information("[DB-PNL-CALL] Calling AddFeeAsync date={Date} fee={Fee:F2}", now.Date, fee);
                        try 
                        { 
                            _ = _pnlRepo.AddFeeAsync(now, fee, CancellationToken.None)
                                .ContinueWith(t =>
                                {
                                    if (t.IsFaulted)
                                    {
                                        _log.Error(t.Exception, "[DB-PNL-ERROR] AddFeeAsync failed date={Date} fee={Fee:F2}", now.Date, fee);
                                    }
                                    else
                                    {
                                        _log.Information("[DB-PNL-SUCCESS] AddFeeAsync completed date={Date} fee={Fee:F2}", now.Date, fee);
                                    }
                                });
                        }
                        catch (Exception ex) 
                        { 
                            _log.Warning(ex, "[DB-PNL] AddFee scheduling failed date={Date} fee={Fee:F2}", now.Date, fee); 
                        }
                    }
                    else
                    {
                        _log.Warning("[DB-PNL-SKIP] _pnlRepo is NULL - skipping AddFeeAsync date={Date} fee={Fee:F2}", now.Date, fee);
                    }

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(brokerId))
                        {
                            foreach (var po in _orders.Snapshot())
                            {
                                if (po.BrokerOrderId == brokerId)
                                {
                                    _orders.TrySetFee(po.CorrelationId, fee, execId: null);
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[ORD-UPD] tagging fee failed");
                    }

                    if (string.Equals(status, "Commission", StringComparison.OrdinalIgnoreCase))
                        return;
                }

                // ============================================================
                // 0.5) DUPLICATE TERMINAL SPAM GUARD (IBKR šalje duplikate posle terminala)
                // NOTE: This check is AFTER commission handling to ensure commissions are always processed
                // ============================================================
                var terminalTtl = TimeSpan.FromMinutes(2);

                if (IsTerminalDuplicate(brokerId, now, terminalTtl))
                {
                    // Namerno bez warn-a (ovo je IBKR spam posle terminalnog stanja)
                    return;
                }

                // ============================================================
                // 1.5) FILL-LIKE STATUS WITHOUT PENDING (normalno posle cleanup-a)
                // ============================================================
                var isFillStatus =
                    status.Equals("Filled", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase) ||
                    status.IndexOf("partial", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isFillStatus && poByBid is null)
                {
                    // Debug-only; nema warn-a jer IBKR često šalje duplikate posle uklanjanja pending-a
                    if (res.FilledQuantity > 0)
                    {
                        _log.Debug("[ORD-UPD] fill-status without pending status={Status} brokerId={Bid} cum={Cum} avg={Avg}",
                            status, brokerId, res.FilledQuantity, res.AverageFillPrice);
                    }
                }

                // ============================================================
                // 1.7) PARTIAL FILL - ne diramo pending!
                // ============================================================
                bool isPartial =
                    status.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase) ||
                    status.IndexOf("partial", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isPartial)
                {
                    if (poByBid == null)
                    {
                        _log.Debug("[ORD-UPD] PARTIAL without pending brokerId={Bid} cum={Cum} avg={Px}",
                            brokerId, res.FilledQuantity, res.AverageFillPrice);
                        return;
                    }

                    // ===== PATCH: "partial" ali cum >= target => tretiraj kao FILLED (anti-downgrade)
                    const decimal QtyEps = 0.0000001m;
                    if (res.FilledQuantity >= poByBid.Req.Quantity - QtyEps)
                    {
                        status = "Filled"; // pusti da padne u Filled granu ispod
                    }
                    else
                    {
                        if (_orderRepo != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var msg = $"cum={res.FilledQuantity:F6} avg={res.AverageFillPrice:F4}";
                                    await _orderRepo.UpdateStatusAsync(
                                        id: poByBid.CorrelationId,
                                        status: "partially_filled",
                                        lastMsg: msg,
                                        ct: CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    _log.Warning(ex, "[DB-ORDERS] update partial failed");
                                }
                            });
                        }

                        _log.Information("[ORD-UPD] PARTIAL brokerId={Bid} cum={Cum} avg={Px}",
                            brokerId, res.FilledQuantity, res.AverageFillPrice);

                        return;
                    }
                }

                // ============================================================
                // 2) CANCELED
                // ============================================================
                if (status.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
                {
                    if (poByBid == null)
                    {
                        // Duplikat posle terminala ili nismo imali pending (npr. restart)
                        SyncDbStatusByBrokerId("canceled", res.Message);
                        MarkTerminal(brokerId, now);
                        ClearCancelRateLimit(brokerId);
                        _log.Debug("[ORD-UPD] CANCELED without pending brokerId={Bid}", brokerId);
                        return;
                    }

                    if (_orders.TryRemove(poByBid.CorrelationId, out var removed) && removed != null)
                    {
                        if (removed.ReservedUsd > 0m)
                            _cashService.Unreserve(removed.ReservedUsd);

                        if (_exposure != null && removed.Req.Side == OrderSide.Buy)
                        {
                            var px = removed.Req.LimitPrice ?? 0m;
                            var usd = removed.Req.Quantity * px;
                            if (usd > 0m)
                                _exposure.Release(removed.Req.Symbol, usd);
                        }

                        if (removed.Req.CorrelationId.StartsWith("exit-", StringComparison.OrdinalIgnoreCase))
                        {
                            lock (_sync) _exitPending.Remove(removed.Req.Symbol.Ticker);
                        }

                        RollbackDayGuardCountIfUnfilledEntry(removed, now, "canceled");
                        ClearCumFilled(removed.CorrelationId);
                        ClearCancelRequested(removed.CorrelationId);
                        FlushDiscordFillNotification(removed.CorrelationId, terminalStatus: "canceled");

                        _log.Information("[ORD-UPD] CANCELED brokerId={Bid} corr={Corr}",
                            brokerId, removed.CorrelationId);

                        if (_orderRepo != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _orderRepo.MarkCanceledAsync(
                                        id: removed.CorrelationId,
                                        canceledUtc: now,
                                        ct: CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    _log.Warning(ex, "[DB-ORDERS] MarkCanceled failed corr={Corr}", removed.CorrelationId);
                                }
                            });
                        }
                    }

                    MarkTerminal(brokerId, now);
                    ClearCancelRateLimit(brokerId);
                    return;
                }

                // ============================================================
                // 2b) REJECTED
                // ============================================================
                if (status.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
                {
                    if (poByBid == null)
                    {
                        SyncDbStatusByBrokerId("rejected", res.Message ?? "rejected");
                        MarkTerminal(brokerId, now);
                        ClearCancelRateLimit(brokerId);
                        _log.Debug("[ORD-UPD] REJECTED without pending brokerId={Bid} msg={Msg}", brokerId, res.Message ?? "n/a");
                        return;
                    }

                    if (_orders.TryRemove(poByBid.CorrelationId, out var removed) && removed != null)
                    {
                        if (removed.ReservedUsd > 0m)
                            _cashService.Unreserve(removed.ReservedUsd);

                        if (_exposure != null && removed.Req.Side == OrderSide.Buy)
                        {
                            var px = removed.Req.LimitPrice ?? 0m;
                            var usd = removed.Req.Quantity * px;
                            if (usd > 0m)
                                _exposure.Release(removed.Req.Symbol, usd);
                        }

                        if (removed.Req.CorrelationId.StartsWith("exit-", StringComparison.OrdinalIgnoreCase))
                        {
                            lock (_sync) _exitPending.Remove(removed.Req.Symbol.Ticker);
                        }

                        RollbackDayGuardCountIfUnfilledEntry(removed, now, "rejected");
                        ClearCumFilled(removed.CorrelationId);
                        ClearCancelRequested(removed.CorrelationId);
                        FlushDiscordFillNotification(removed.CorrelationId, terminalStatus: "rejected");

                        if (_orderRepo != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _orderRepo.UpdateStatusAsync(
                                        id: removed.CorrelationId,
                                        status: "rejected",
                                        lastMsg: res.Message ?? "rejected",
                                        ct: CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    _log.Warning(ex, "[DB-ORDERS] UpdateStatus(rejected) failed corr={Corr}", removed.CorrelationId);
                                }
                            });
                        }

                        _log.Information("[ORD-UPD] REJECTED brokerId={Bid} corr={Corr}",
                            brokerId, removed.CorrelationId);

                        try { OrderMetrics.Instance.Reject(removed.Req.Symbol.Ticker); }
                        catch { }
                    }

                    MarkTerminal(brokerId, now);
                    ClearCancelRateLimit(brokerId);
                    return;
                }

                // ============================================================
                // 2c) FINAL FILLED - cleanup pending + fallback na missing slice
                // ============================================================
                if (status.Equals("Filled", StringComparison.OrdinalIgnoreCase))
                {
                    var matched = poByBid;

                    if (matched != null)
                    {
                        // FALLBACK: ako ExecutionDetails nisu isporučili poslednji slice ILI nisu uopšte stigli
                        if (res.FilledQuantity > 0)
                        {
                            var corrId = matched.CorrelationId;

                            decimal prevCum;
                            lock (_sync) { _cumFilledByCorrId.TryGetValue(corrId, out prevCum); }

                            var finalCum = (decimal)res.FilledQuantity;
                            var missingQty = finalCum - prevCum;

                            // FIX: Ako nije bilo ExecutionDetails eventa (prevCum == 0), primeni ceo fill
                            // Ovo je kriticno za exit order-e koji se izvrsavaju preko noci/vikenda
                            if (prevCum == 0m && finalCum > 0m)
                            {
                                missingQty = finalCum; // primeni ceo fill
                            }

                            if (missingQty > 0m)
                            {
                                var fillPx = res.AverageFillPrice ?? matched.Req.LimitPrice ?? 0m;

                                if (fillPx > 0m)
                                {
                                    // FIX: Proveri da li je duplikat pre poziva ApplyFillCore
                                    if (IsDuplicateRealFill(corrId, missingQty, fillPx, now))
                                    {
                                        _log.Warning(
                                            "[ORD-UPD-FALLBACK-DUP] Ignoring duplicate fill for {Sym} corr={Corr} missingQty={Qty:F6} px={Px:F4}",
                                            matched.Req.Symbol.Ticker, corrId, missingQty, fillPx);
                                    }
                                    else
                                    {
                                        var sliceReq = new OrderRequest(
                                            symbol: matched.Req.Symbol,
                                            side: matched.Req.Side,
                                            type: matched.Req.Type,
                                            quantity: missingQty,
                                            limitPrice: matched.Req.LimitPrice,
                                            tif: matched.Req.Tif,
                                            correlationId: matched.Req.CorrelationId,
                                            timestampUtc: matched.Req.TimestampUtc,
                                            ocoGroupId: matched.Req.OcoGroupId,
                                            ocoStopPrice: matched.Req.OcoStopPrice,
                                            stopPrice: matched.Req.StopPrice,
                                            isExit: matched.Req.IsExit
                                        );

                                        ApplyFillCore(sliceReq, fillPx, now, isPaper: false);

                                        _log.Information(
                                            "[ORD-UPD-FALLBACK] Applied missing slice corr={Corr} missingQty={Qty:F6} finalCum={FinalCum:F6} prevCum={PrevCum:F6} px={Px:F4}",
                                            corrId, missingQty, finalCum, prevCum, fillPx);
                                    }
                                }
                                else
                                {
                                    _log.Warning(
                                        "[ORD-UPD-FALLBACK] Cannot apply missing slice corr={Corr} missingQty={Qty:F6} finalCum={FinalCum:F6} prevCum={PrevCum:F6} (no price)",
                                        corrId, missingQty, finalCum, prevCum);
                                }
                            }
                        }

                        if (_orders.TryRemove(matched.CorrelationId, out var removed) && removed != null)
                        {
                            if (removed.ReservedUsd > 0m)
                                _cashService.Unreserve(removed.ReservedUsd);

                            if (removed.Req.CorrelationId.StartsWith("exit-", StringComparison.OrdinalIgnoreCase))
                            {
                                lock (_sync) _exitPending.Remove(removed.Req.Symbol.Ticker);
                            }

                            _log.Information("[ORD-UPD] FINAL FILLED brokerId={Bid} corr={Corr}",
                                brokerId, removed.CorrelationId);

                            if (_orderRepo != null)
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var msg = $"cum={res.FilledQuantity:F6} avg={res.AverageFillPrice:F4}";
                                        await _orderRepo.UpdateStatusAsync(
                                            id: removed.CorrelationId,
                                            status: "filled",
                                            lastMsg: msg,
                                            ct: CancellationToken.None);
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.Warning(ex, "[DB-ORDERS] UpdateStatus(filled) failed corr={Corr}", removed.CorrelationId);
                                    }
                                });
                            }

                            ClearCancelRequested(removed.CorrelationId);
                            ClearCumFilled(removed.CorrelationId);
                            FlushDiscordFillNotification(removed.CorrelationId, terminalStatus: "filled");
                        }

                        MarkTerminal(brokerId, now);
                    }
                    else
                    {
                        // Ovo je normalno posle cleanup-a ili restart-a; nema warn-a.
                        var msg = $"cum={res.FilledQuantity:F6} avg={res.AverageFillPrice:F4}";
                        SyncDbStatusByBrokerId("filled", msg);
                        MarkTerminal(brokerId, now);
                        _log.Debug("[ORD-UPD] FILLED without pending brokerId={Bid} cum={Cum} avg={Avg}",
                            brokerId, res.FilledQuantity, res.AverageFillPrice);
                    }

                    ClearCancelRateLimit(brokerId);
                    return;
                }

                // ============================================================
                // 3) Ostali statusi - samo log
                // ============================================================
                _log.Information("[ORD-UPD] {Status} brokerId={Bid} filled={Filled} px={Px}",
                    status, brokerId, res.FilledQuantity, res.AverageFillPrice);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[ORD-UPD] failed to process broker event");
            }
        }
        private void ApplyEstimatedCommission(decimal notional)
        {
            var est = _fees.EstimatedPerOrderUsd;
            if (est > 0m)
            {
                _cashService.OnCommissionPaid(est);
                _log.Information("[EST-COMM] estimated commission {Fee:F2} applied", est);
            }
        }
        private decimal NormalizeQty(Symbol symbol, decimal rawQty)
        {
            if (rawQty <= 0m)
                return 0m;

            var cfg = _settings.Symbols?.FirstOrDefault(s =>
                string.Equals(s.Symbol, symbol.Ticker, StringComparison.OrdinalIgnoreCase));

            if (cfg is null)
            {
                var whole = Math.Floor(rawQty);
                return whole <= 0m ? 0m : whole;
            }

            if (cfg.SupportsFractional)
            {
                var step = cfg.MinQty > 0m ? cfg.MinQty : 0.001m;

                var steps = Math.Floor(rawQty / step);
                var norm = steps * step;

                return norm <= 0m ? 0m : norm;
            }

            var min = cfg.MinQty > 0m ? cfg.MinQty : 1m;
            var units = Math.Floor(rawQty / min);
            var normalized = units * min;

            return normalized <= 0m ? 0m : normalized;
        }
        private void RollbackPendingAndReserves(OrderRequest req)
        {
            if (_orders.TryRemove(req.CorrelationId, out var po) && po is not null)
            {
                // 1) vrati keš
                if (po.ReservedUsd > 0m)
                {
                    try
                    {
                        _cashService.Unreserve(po.ReservedUsd);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[ROLLBACK] unreserve failed for {Sym}", req.Symbol.Ticker);
                    }
                }

                // 2) vrati exposure ako je BUY
                if (_exposure is not null && req.Side == OrderSide.Buy)
                {
                    var px = req.LimitPrice ?? 0m;
                    var usd = req.Quantity * px;
                    if (usd > 0m) _exposure.Release(req.Symbol, usd);
                }
                RollbackDayGuardCountIfUnfilledEntry(po, DateTime.UtcNow, "place-rollback");
                // 3) ako je EXIT
                if (req.CorrelationId.StartsWith("exit-", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_sync)
                    {
                        _exitPending.Remove(req.Symbol.Ticker);
                    }
                }
                // 4) upisi u DB da je rollback uradjen
                if (_orderRepo is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _orderRepo.UpdateStatusAsync(
                                id: req.CorrelationId,
                                status: "place-rolled-back",
                                lastMsg: "reserve/exposure rolled back after place failure",
                                ct: CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning(ex, "[DB-ORDERS] UpdateStatus(place-rolled-back) failed");
                        }
                    });
                }
                _log.Information("[ROLLBACK] {Sym} corr={Corr} reserved={Res:F2} released", req.Symbol.Ticker, req.CorrelationId, po.ReservedUsd);
            }
        }

        private void TrackDiscordFillNotification(
            OrderRequest req,
            decimal notional,
            decimal realizedPnl,
            decimal fillPx,
            bool isPaper,
            string? exitReason)
        {
            if (_discordNotifier is null)
            {
                _log.Debug("[DISCORD] Discord notifier is NULL - skipping fill notification for {Symbol}", req.Symbol.Ticker);
                return;
            }

            if (isPaper)
            {
                SendDiscordFillNotification(
                    side: req.Side,
                    symbol: req.Symbol.Ticker,
                    quantity: req.Quantity,
                    averagePrice: fillPx,
                    notional: notional,
                    realizedPnl: realizedPnl,
                    exchange: req.Symbol.Exchange ?? "SMART",
                    isPaper: true,
                    exitReason: exitReason,
                    correlationId: req.CorrelationId,
                    sliceCount: 1);
                return;
            }

            var corr = req.CorrelationId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(corr))
            {
                SendDiscordFillNotification(
                    side: req.Side,
                    symbol: req.Symbol.Ticker,
                    quantity: req.Quantity,
                    averagePrice: fillPx,
                    notional: notional,
                    realizedPnl: realizedPnl,
                    exchange: req.Symbol.Exchange ?? "SMART",
                    isPaper: false,
                    exitReason: exitReason,
                    correlationId: null,
                    sliceCount: 1);
                return;
            }

            lock (_sync)
            {
                if (!_discordFillByCorrId.TryGetValue(corr, out var aggregate))
                {
                    aggregate = new AggregatedDiscordFillNotification
                    {
                        Symbol = req.Symbol.Ticker,
                        Side = req.Side,
                        Exchange = req.Symbol.Exchange ?? "SMART",
                        IsPaper = false
                    };
                    _discordFillByCorrId[corr] = aggregate;
                }

                aggregate.TotalQuantity += req.Quantity;
                aggregate.TotalNotional += notional;
                aggregate.TotalRealizedPnl += realizedPnl;
                aggregate.SliceCount++;

                if (!string.IsNullOrWhiteSpace(exitReason))
                    aggregate.ExitReason = exitReason;
            }

            _log.Information(
                "[DISCORD] Aggregated fill slice corr={Corr} sym={Sym} side={Side} qty={Qty:F6} px={Px:F4}",
                corr, req.Symbol.Ticker, req.Side, req.Quantity, fillPx);
        }

        private void FlushDiscordFillNotification(string correlationId, string terminalStatus)
        {
            if (_discordNotifier is null || string.IsNullOrWhiteSpace(correlationId))
                return;

            AggregatedDiscordFillNotification? aggregate = null;
            lock (_sync)
            {
                if (_discordFillByCorrId.TryGetValue(correlationId, out var found))
                {
                    aggregate = found;
                    _discordFillByCorrId.Remove(correlationId);
                }
            }

            if (aggregate is null || aggregate.TotalQuantity <= 0m)
                return;

            var averagePrice = aggregate.TotalQuantity > 0m
                ? aggregate.TotalNotional / aggregate.TotalQuantity
                : 0m;

            _log.Information(
                "[DISCORD] Flushing aggregated notification corr={Corr} side={Side} sym={Sym} qty={Qty:F6} avgPx={Px:F4} slices={Slices} terminal={Terminal}",
                correlationId,
                aggregate.Side,
                aggregate.Symbol,
                aggregate.TotalQuantity,
                averagePrice,
                aggregate.SliceCount,
                terminalStatus);

            SendDiscordFillNotification(
                side: aggregate.Side,
                symbol: aggregate.Symbol,
                quantity: aggregate.TotalQuantity,
                averagePrice: averagePrice,
                notional: aggregate.TotalNotional,
                realizedPnl: aggregate.TotalRealizedPnl,
                exchange: aggregate.Exchange,
                isPaper: aggregate.IsPaper,
                exitReason: aggregate.ExitReason,
                correlationId: correlationId,
                sliceCount: aggregate.SliceCount);
        }

        private void SendDiscordFillNotification(
            OrderSide side,
            string symbol,
            decimal quantity,
            decimal averagePrice,
            decimal notional,
            decimal realizedPnl,
            string exchange,
            bool isPaper,
            string? exitReason,
            string? correlationId,
            int sliceCount)
        {
            if (_discordNotifier is null)
                return;

            var corrForLog = correlationId ?? "n/a";
            _ = Task.Run(async () =>
            {
                try
                {
                    if (side == OrderSide.Buy)
                    {
                        _log.Information(
                            "[DISCORD] Sending BUY notification for {Symbol} avgPx={Price:F4} qty={Qty:F6} exchange={Ex} corr={Corr} slices={Slices}",
                            symbol, averagePrice, quantity, exchange, corrForLog, sliceCount);
                        await _discordNotifier.NotifyBuyAsync(
                            symbol: symbol,
                            quantity: quantity,
                            price: averagePrice,
                            notional: notional,
                            exchange: exchange,
                            isPaper: isPaper,
                            ct: CancellationToken.None);
                    }
                    else
                    {
                        _log.Information(
                            "[DISCORD] Sending SELL notification for {Symbol} avgPx={Price:F4} qty={Qty:F6} pnl={Pnl:F2} exchange={Ex} corr={Corr} slices={Slices}",
                            symbol, averagePrice, quantity, realizedPnl, exchange, corrForLog, sliceCount);
                        await _discordNotifier.NotifySellAsync(
                            symbol: symbol,
                            quantity: quantity,
                            price: averagePrice,
                            notional: notional,
                            realizedPnl: realizedPnl,
                            exchange: exchange,
                            isPaper: isPaper,
                            exitReason: exitReason,
                            ct: CancellationToken.None);
                    }

                    _log.Information("[DISCORD] {Side} notification sent successfully for {Symbol} corr={Corr}", side, symbol, corrForLog);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[DISCORD] Failed to send {Side} notification for {Symbol} corr={Corr}", side, symbol, corrForLog);
                }
            });
        }

        private sealed class AggregatedDiscordFillNotification
        {
            public string Symbol { get; init; } = string.Empty;
            public OrderSide Side { get; init; }
            public string Exchange { get; init; } = "SMART";
            public bool IsPaper { get; init; }
            public decimal TotalQuantity { get; set; }
            public decimal TotalNotional { get; set; }
            public decimal TotalRealizedPnl { get; set; }
            public int SliceCount { get; set; }
            public string? ExitReason { get; set; }
        }

        private void FireAndForgetCancel(string brokerOrderId, string symbol)
        {
            if (_orderService is null)
                return;

            var now = DateTime.UtcNow;

            // rate limiting po brokerOrderId
            lock (_sync)
            {
                if (_lastCancelUtcByBrokerId.TryGetValue(brokerOrderId, out var last))
                {
                    var delta = now - last;
                    if (delta < _minCancelSpacing)
                    {
                        _log.Warning("[CANCEL-RATE-LIMIT] skip cancel brokerId={Bid} sym={Sym} delta={DeltaMs}ms min={MinMs}ms",
                            brokerOrderId,
                            symbol,
                            delta.TotalMilliseconds,
                            _minCancelSpacing.TotalMilliseconds
                        );
                        return;
                    }
                }
                _lastCancelUtcByBrokerId[brokerOrderId] = now;
            }
            
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(_brokerCancelTimeout);
                    await _orderService.CancelAsync(brokerOrderId).ConfigureAwait(false);
                    _log.Information("[CANCEL] brokerId={Bid} sym={Sym} sent", brokerOrderId, symbol);
                }
                catch (OperationCanceledException)
                {
                    _log.Warning("[CANCEL-TIMEOUT] brokerId={Bid} sym={Sym}", brokerOrderId, symbol);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[CANCEL-ERR] brokerId={Bid} sym={Sym}", brokerOrderId, symbol);
                }
            });
        }
        private void SafeReserve(decimal amount, DateTime utcNow, string reason)
        {
            try
            {
                _cashService.Reserve(amount, utcNow);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CASH] reserve failed {Amt:F2} reason={Reason}", amount, reason);
            }
        }
        private bool HasPendingByCorrelation(string correlationId)
        {
            if (string.IsNullOrWhiteSpace(correlationId)) return false;
            var pendings = _orders.Snapshot();
            foreach (var p in pendings)
            {
                if (p?.Req is null) continue;
                if (string.Equals(p.Req.CorrelationId, correlationId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
        private void OnQuoteCached(MarketQuote q)
        {
            if (q?.Symbol is null) return;

            lock (_sync)
            {
                _lastQuotes[q.Symbol.Ticker] = q;
            }
        }
        
        private void OnQuoteForTickProfiler(MarketQuote q)
        {
            try
            {
                if (q?.Symbol is null) return;
                
                var utcNow = DateTime.UtcNow;
                var quoteAgeSeconds = (utcNow - q.TimestampUtc).TotalSeconds;
                
                // Izračunaj spread bps
                double spreadBps = 0;
                if (q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value > 0 && q.Ask.Value > 0)
                {
                    var mid = (q.Bid.Value + q.Ask.Value) / 2m;
                    var spread = q.Ask.Value - q.Bid.Value;
                    spreadBps = (double)((spread / mid) * 10000m); // konvertuj u bps
                }
                
                // Izračunaj ATR fraction
                double atrFrac = 0;
                var ticker = q.Symbol.Ticker;
                if (_atr.TryGetValue(ticker, out var atrState) && atrState.Atr.HasValue && q.Last.HasValue && q.Last.Value > 0)
                {
                    atrFrac = (double)(atrState.Atr.Value / q.Last.Value);
                }
                
                _tickProfiler.RecordTick(ticker, utcNow, quoteAgeSeconds, spreadBps, atrFrac);
            }
            catch (Exception ex)
            {
                // Tick profiler ne sme da sruši trading engine
                _log.Warning(ex, "[TICK-PROFILER] Failed to record tick for {Sym}", q?.Symbol?.Ticker ?? "unknown");
            }
        }
        private bool TryGetQuote(string ticker, DateTime utcNow, out MarketQuote q, out string? reason)
        {
            q = default!;
            reason = null;

            lock (_sync)
            {
                _lastQuotes.TryGetValue(ticker, out q);
            }

            if (q is null)
            {
                reason = "no-quote";
                return false;
            }

            var age = utcNow - q.TimestampUtc;

            // odavde novo
            var isRth   = IsRegularTradingHoursNy(utcNow);
            var maxAge  = isRth ? MaxQuoteAgeRth : MaxQuoteAgeExtended;

            if (age > maxAge)
            {
                decimal? mid = null;
                if (q.Bid.HasValue && q.Ask.HasValue && q.Bid > 0m && q.Ask > 0m)
                    mid = (q.Bid.Value + q.Ask.Value) / 2m;

                var bidStr = q.Bid?.ToString("F4") ?? "n/a";
                var askStr = q.Ask?.ToString("F4") ?? "n/a";
                var midStr = mid?.ToString("F4") ?? "n/a";

                reason =
                    $"stale-quote:{age.TotalSeconds:F1}s" +
                    $";bid={bidStr};ask={askStr};mid={midStr}";

                return false;
            }
            // do ovde novo

            if (q.Bid is not { } bid || q.Ask is not { } ask || bid <= 0m || ask <= 0m)
            {
                reason = "no-nbbo";
                return false;
            }

            var mid2 = (bid + ask) / 2m;
            if (mid2 <= 0m)
            {
                reason = "invalid-mid";
                return false;
            }

            var spread = ask - bid;
            var spreadFrac = spread / mid2;

            if (spreadFrac > MaxSpreadFraction)
            {
                reason = $"spread-too-wide:{spreadFrac:P2}";
                return false;
            }

            return true;
        }
        private async Task EvaluateIbkrEodSkimAsync(DateTime nowUtc)
        {
            if (!_ibkrEodSkimOptions.Enabled)
                return;

            // Milestone 1/2: dry-run only wiring. Live mode intentionally not enabled here yet.
            if (!_ibkrEodSkimOptions.DryRun)
            {
                _log.Warning(
                    "[EOD-SKIM] Enabled with DryRun=false but live execution is not wired yet in orchestrator. Skipping skim evaluation.");
                return;
            }

            if (_orderService is null || !_isRealMode)
                return;

            try
            {
                var positions = _positionBook.Snapshot();
                var live = positions
                    .Where(p => p.Quantity > 0m)
                    .Select(p => new IbkrEodSkimPositionCandidate(
                        Symbol: p.Symbol,
                        Quantity: p.Quantity,
                        AveragePrice: p.AveragePrice,
                        IsExternalIbkrPosition: IsExternalPosition(p.Symbol)))
                    .ToArray();

                var context = new IbkrEodSkimContext
                {
                    UtcNow = nowUtc,
                    MarketCloseUtc = GetEodSkimMarketCloseUtc(nowUtc),
                    IsRealMode = _isRealMode && _orderService is not null,
                    IsIbkrMode = _externalPositions is not null,
                    EstimatedPerOrderFeeUsd = _fees.EstimatedPerOrderUsd,
                    // V1 buffer for dry-run observability; can become config later.
                    EstimatedSlippageBufferUsd = _fees.EstimatedPerOrderUsd,
                    OpenPositions = live,
                    TryGetQuote = (symbol, utc) =>
                    {
                        if (TryGetQuote(symbol, utc, out var q, out var reason))
                            return new IbkrEodSkimQuoteLookupResult(true, q, null);

                        return new IbkrEodSkimQuoteLookupResult(false, null, reason);
                    }
                };

                await _ibkrEodSkim.EvaluateAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Fail-safe: EOD skim must never break baseline heartbeat logic.
                _log.Warning(ex, "[EOD-SKIM] Dry-run evaluation failed (fail-safe skip)");
            }
        }
        private DateTime GetEodSkimMarketCloseUtc(DateTime utcNow)
        {
            if (_settings.TradeEndLocalNy.HasValue)
            {
                var closeNyTz = GetNewYorkTz();
                var closeNyNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, closeNyTz);
                var closeLocal = closeNyNow.Date + _settings.TradeEndLocalNy.Value;
                if (closeLocal <= closeNyNow)
                    closeLocal = closeLocal.AddDays(1);
                return TimeZoneInfo.ConvertTimeToUtc(closeLocal, closeNyTz);
            }

            // Prefer configured Trading.RthEndUtc if present (matches current engine operating window).
            if (_settings.RthEndUtc.HasValue)
            {
                var close = utcNow.Date + _settings.RthEndUtc.Value;
                if (close <= utcNow)
                    close = close.AddDays(1);
                return close;
            }

            // Fallback: compute regular 16:00 NY market close (DST-safe via timezone conversion).
            var nyTz = GetNewYorkTz();
            var nyNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, nyTz);
            var nyCloseLocal = new DateTime(
                nyNow.Year,
                nyNow.Month,
                nyNow.Day,
                16, 0, 0,
                DateTimeKind.Unspecified);

            if (nyCloseLocal <= nyNow)
                nyCloseLocal = nyCloseLocal.AddDays(1);

            return TimeZoneInfo.ConvertTimeToUtc(nyCloseLocal, nyTz);
        }
        private bool HasConfiguredTradingWindow()
        {
            return _tradeStartOffsetFromOpenNy is not null
                || _tradeEndLocalNy is not null
                || _rthStartUtc is not null
                || _rthEndUtc is not null;
        }
        private bool TryGetRequiredSignalPriorityScore(int currentTradeCountTotal, out decimal requiredScore)
        {
            requiredScore = 0m;
            var hasRequirement = false;

            if (_settings.MaxTradesTotal <= 0)
                return false;

            if (currentTradeCountTotal >= 1 && _settings.MinSignalPriorityScoreAfterFirstTrade.HasValue)
            {
                requiredScore = _settings.MinSignalPriorityScoreAfterFirstTrade.Value;
                hasRequirement = true;
            }

            var isLastTradeSlot =
                (_settings.MaxTradesTotal == 1 && currentTradeCountTotal == 0)
                || currentTradeCountTotal >= _settings.MaxTradesTotal - 1;

            if (isLastTradeSlot && _settings.MinSignalPriorityScoreForLastTradeSlot.HasValue)
            {
                requiredScore = hasRequirement
                    ? Math.Max(requiredScore, _settings.MinSignalPriorityScoreForLastTradeSlot.Value)
                    : _settings.MinSignalPriorityScoreForLastTradeSlot.Value;
                hasRequirement = true;
            }

            return hasRequirement;
        }
        private static decimal? ComputeSignalPriorityScore(TradeSignal signal)
        {
            if (!signal.SuggestedLimitPrice.HasValue || signal.SuggestedLimitPrice.Value <= 0m)
                return null;

            var price = signal.SuggestedLimitPrice.Value;
            var score = 50m;

            if (signal.Slope5.HasValue)
            {
                var slope5Bps = (signal.Slope5.Value / price) * 10000m;
                score += ClampDecimal(slope5Bps * 18m, -25m, 25m);
            }
            else
            {
                score -= 8m;
            }

            if (signal.Slope20.HasValue)
            {
                var slope20Bps = (signal.Slope20.Value / price) * 10000m;
                score += ClampDecimal(slope20Bps * 10m, -12m, 12m);
            }
            else
            {
                score -= 4m;
            }

            if (signal.SpreadBps.HasValue)
            {
                score += ClampDecimal((12m - signal.SpreadBps.Value) * 2.2m, -18m, 18m);
            }
            else
            {
                score -= 4m;
            }

            if (signal.ActivityTicks.HasValue)
            {
                score += ClampDecimal((((decimal)signal.ActivityTicks.Value) - 75m) / 3m, -12m, 18m);
            }
            else
            {
                score -= 4m;
            }

            score += signal.Regime?.ToUpperInvariant() switch
            {
                "HIGH" => 8m,
                "NORMAL" => 3m,
                "LOW" => -6m,
                _ => 0m
            };

            return ClampDecimal(score, 0m, 100m);
        }
        private static decimal ClampDecimal(decimal value, decimal min, decimal max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
        private bool IsInsideConfiguredTradingWindow(DateTime utcNow, out string details)
        {
            var nyTz = GetNewYorkTz();
            var nyNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, nyTz);

            DateTime? startUtc = null;
            string? startSource = null;

            if (_tradeStartOffsetFromOpenNy.HasValue)
            {
                var nyOpenLocal = nyNow.Date.AddHours(9).AddMinutes(30);
                startUtc = TimeZoneInfo.ConvertTimeToUtc(nyOpenLocal + _tradeStartOffsetFromOpenNy.Value, nyTz);
                startSource = $"ny-open+{_tradeStartOffsetFromOpenNy.Value}";
            }
            else if (_rthStartUtc.HasValue)
            {
                startUtc = utcNow.Date + _rthStartUtc.Value;
                startSource = $"utc={_rthStartUtc.Value}";
            }

            DateTime? endUtc = null;
            string? endSource = null;

            if (_tradeEndLocalNy.HasValue)
            {
                var nyEndLocal = nyNow.Date + _tradeEndLocalNy.Value;
                endUtc = TimeZoneInfo.ConvertTimeToUtc(nyEndLocal, nyTz);
                endSource = $"ny={_tradeEndLocalNy.Value}";
            }
            else if (_rthEndUtc.HasValue)
            {
                endUtc = utcNow.Date + _rthEndUtc.Value;
                endSource = $"utc={_rthEndUtc.Value}";
            }

            if (startUtc.HasValue && utcNow < startUtc.Value)
            {
                details = $"before-start nowUtc={utcNow:O} nowNy={nyNow:O} startUtc={startUtc.Value:O} startSource={startSource}";
                return false;
            }

            if (endUtc.HasValue && utcNow > endUtc.Value)
            {
                details = $"after-end nowUtc={utcNow:O} nowNy={nyNow:O} endUtc={endUtc.Value:O} endSource={endSource}";
                return false;
            }

            details = $"inside nowUtc={utcNow:O} nowNy={nyNow:O} startSource={startSource ?? "none"} endSource={endSource ?? "none"}";
            return true;
        }
        private static TimeZoneInfo GetNewYorkTz()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        }
        public static bool IsRegularTradingHoursNy(DateTime utcNow)
        {
            var ny = TimeZoneInfo.ConvertTimeFromUtc(utcNow, GetNewYorkTz());

            if (ny.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                return false;

            var t = ny.TimeOfDay;
            return t >= TimeSpan.FromHours(9.5) && t < TimeSpan.FromHours(16);
        }
        private void UpdateAtrOnQuote(MarketQuote q)
        {
            try
            {
                if (q?.Symbol is null || q.Last is null) return;

                var key = q.Symbol.Ticker;

                if (!_atr.TryGetValue(key, out var s))
                    _atr[key] = s = new AtrState();

                var px = q.Last.Value;

                if (s.Prev is null)
                {
                    s.Prev = px;
                    return;
                }

                var tr = Math.Abs(px - s.Prev.Value);
                s.Tr.Enqueue(tr);
                if (s.Tr.Count > 14) s.Tr.Dequeue();

                if (s.Tr.Count == 14)
                    s.Atr = s.Tr.Average();

                s.Prev = px;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[ATR] update failed");
            }
        }
        private bool IsInCooldown(Symbol symbol, DateTime utcNow, out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;

            lock (_sync)
            {
                if (_lastLossUtc.TryGetValue(symbol.Ticker, out var lossTime))
                {
                    var until = lossTime + _symbolCooldownAfterLoss;
                    if (utcNow < until)
                    {
                        remaining = until - utcNow;
                        return true;
                    }
                }
            }

            return false;
        }
        // REAL: SendExit se koristi samo za:
        //  - Swing auto-exit (MaxHoldingDays / WeekendCutoff) kao override
        //  - manual/recovery scenario (ako ga ikad dodaš)
        // PAPER: koristi se i iz quote-based heuristike (TP/SL/TRAIL/TIME)
        private void SendExit(string symbol, decimal qty, decimal px, string reason, string? corrPrefix = null)
        {

            // NOVO: apsolutna zastita za External/IBKR pozicije
            if (IsExternalPosition(symbol))
            {
                _log.Warning(
                    "[SEND-EXIT-BLOCKED-EXTERNAL] sym={Sym} qty={Qty:F4} px={Px:F2} reason={Reason}",
                    symbol,
                    qty,
                    px,
                    reason);
                return;
            }


            if (_orderService != null && !_swingConfig.AutoExitReal)
            {
                _log.Warning("[SEND-EXIT] called in REAL while AutoExitReal=false (caller bug?) sym={Sym}", symbol);
            }

            var exitTif = SwingHelpers.IsSwingMode(_swingConfig)
                ? TimeInForce.Gtc
                : TimeInForce.Day;

            var nowUtc = DateTime.UtcNow;

            // koren za correlationId
            var prefix = string.IsNullOrWhiteSpace(corrPrefix) ? "exit" : corrPrefix;
            if (!prefix.EndsWith("-", StringComparison.Ordinal))
                prefix += "-";

            var corrId = $"{prefix}{Guid.NewGuid():N}";

            var req = new OrderRequest(
                symbol: new Symbol(symbol),
                side: OrderSide.Sell,
                type: OrderType.Limit,
                quantity: qty,
                limitPrice: px,
                tif: exitTif,
                correlationId: corrId,
                timestampUtc: nowUtc,
                isExit: true
            );

            // Prometheus: TP / SL exit metrike (na osnovu reason stringa)
            try
            {
                if (reason.StartsWith("TP-", StringComparison.OrdinalIgnoreCase))
                {
                    OrderMetrics.Instance.ExitTP(symbol);
                }
                else if (reason.StartsWith("SL-", StringComparison.OrdinalIgnoreCase))
                {
                    OrderMetrics.Instance.ExitSL(symbol);
                }
                // TIME EXIT i TRAIL za sada ne brojimo posebno
            }
            catch
            {
                // metrics nikad ne sme da sruši trading engine
            }

            // Rezervacija fee-a za EXIT je best-effort.
            decimal reserved = 0m;
            try
            {
                reserved = _fees.EstimatedPerOrderUsd;
                _cashService.Reserve(reserved, nowUtc);
            }
            catch (Exception ex)
            {
                reserved = 0m; // ponašamo se kao da ništa nije rezervisano
                _log.Warning(ex, "[CASH] EXIT reserve failed (sym={Sym}, qty={Qty}, px={Px:F2}). Proceeding without reserve", symbol, qty, px);
            }

            lock (_sync)
            {
                _exitPending.Add(symbol);
            }

            _orders.TryAdd(new PendingOrder(req, reserved, nowUtc));

            if (_orderService is null)
            {
                _log.Information("[PAPER-EXIT] SELL {Sym} x{Qty:F6} @ {Px:F2} reason={Reason}", symbol, qty, px, reason);
                _paperSim.Register(req);
            }
            else
            {
                _log.Information("[REAL-EXIT] SELL {Sym} x{Qty:F6} @ {Px:F2} reason={Reason}", symbol, qty, px, reason);
                _ = PlaceRealAsync(req);
            }
        }
        private void ClearCumFilled(string corrId)
        {
            if (string.IsNullOrWhiteSpace(corrId)) return;
            lock (_sync)
            {
                _cumFilledByCorrId.Remove(corrId);
            }
        }
        private decimal GetCumFilledForCorrelation(string corrId)
        {
            if (string.IsNullOrWhiteSpace(corrId))
                return 0m;

            lock (_sync)
            {
                return _cumFilledByCorrId.TryGetValue(corrId, out var cum) ? cum : 0m;
            }
        }
        private void RollbackDayGuardCountIfUnfilledEntry(PendingOrder pending, DateTime nowUtc, string reason)
        {
            if (_dayGuards is null || pending?.Req is null)
                return;

            var req = pending.Req;
            var corr = pending.CorrelationId ?? req.CorrelationId ?? string.Empty;

            var isExit = req.IsExit || corr.StartsWith("exit-", StringComparison.OrdinalIgnoreCase);
            if (isExit || req.Side != OrderSide.Buy)
                return;

            var cumFilled = GetCumFilledForCorrelation(corr);
            if (cumFilled > 0m)
                return; // partial/full fill already happened => count should stay spent

            var beforeTotal = _dayGuards.CurrentTradeCountTotal;
            var beforePerSym = _dayGuards.CurrentTradeCountPerSymbol.TryGetValue(req.Symbol.Ticker, out var beforeSymCount)
                ? beforeSymCount
                : 0;

            _dayGuards.OnEntryOrderVoided(req.Symbol, nowUtc);

            var afterTotal = _dayGuards.CurrentTradeCountTotal;
            var afterPerSym = _dayGuards.CurrentTradeCountPerSymbol.TryGetValue(req.Symbol.Ticker, out var afterSymCount)
                ? afterSymCount
                : 0;

            _log.Information(
                "[DAY-GUARD-COUNT] Rollback entry slot ({Reason}) for {Sym}: Total {Before}->{After} PerSymbol {BeforeSym}->{AfterSym}",
                reason,
                req.Symbol.Ticker,
                beforeTotal,
                afterTotal,
                beforePerSym,
                afterPerSym);
        }
        private (decimal Equity, decimal InPositionsUsd) GetApproxEquity()
        {
            var cash = _lastCash;
            decimal inPosUsd = 0m;

            var positions = _positionBook.Snapshot();
            foreach (var p in positions)
            {
                if (p.Quantity == 0m)
                    continue;

                MarketQuote q;
                lock (_sync)
                {
                    _lastQuotes.TryGetValue(p.Symbol, out q);
                }

                decimal px;

                if (q is not null && q.Bid.HasValue && q.Ask.HasValue && q.Bid > 0m && q.Ask > 0m)
                {
                    // mid NBBO
                    px = (q.Bid.Value + q.Ask.Value) / 2m;
                }
                else if (q is not null && q.Last.HasValue && q.Last > 0m)
                {
                    px = q.Last.Value;
                }
                else
                {
                    // fallback - average price iz pozicije
                    px = p.AveragePrice;
                }

                if (px <= 0m)
                    continue;

                inPosUsd += p.Quantity * px;
            }

            var equity = cash.Free + cash.Settling + inPosUsd;
            return (equity, inPosUsd);
        }
        private bool IsRateLimited(DateTime nowUtc, out string reason)
        {
            lock (_sync)
            {
                // izbaci sve što je starije od prozora
                while (_signalTimestamps.Count > 0 && nowUtc - _signalTimestamps.Peek() > _signalWindow)
                {
                    _signalTimestamps.Dequeue();
                }
                // upiši ovaj signal
                _signalTimestamps.Enqueue(nowUtc);
                // ako smo presli limit - blokiraj
                if (_signalTimestamps.Count > _maxSignalsPerWindow)
                {
                    reason = $"{_signalTimestamps.Count}/{_maxSignalsPerWindow} in {_signalWindow.TotalSeconds:F0}s";
                    return true;
                }
                reason = string.Empty;
                return false;
            }
        }
        public async Task RecoverOnStartupAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            _log.Information("[RECOVERY] Starting state recovery");

            // 1) Cash
            try
            {
                var cs = await _cashService.GetCashStateAsync().ConfigureAwait(false);
                _lastCash = cs;
                _log.Information("[RECOVERY] Cash Free={Free:F2} Settling={Sett:F2} InPos={InPos:F2} Reserved={Res:F2}",
                    cs.Free, cs.Settling, cs.InPositions, cs.Reserved);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[RECOVERY] Failed to refresh cash state");
            }

            // 2) External positions
            if (_externalPositions is not null)
            {
                try { await ImportExternalPositionsNowAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) { _log.Warning(ex, "[RECOVERY] ImportExternalPositionsNowAsync failed"); }
            }
            else
            {
                _log.Information("[RECOVERY] No external positions provider (skipping external sync)");
            }

            // 2.5) DayGuards recovery - učitaj trade counts i PnL iz baze
            if (_dayGuards is not null && _signalRepo is not null && _pnlRepo is not null)
            {
                try
                {
                    var today = now.Date;

                    // Učitaj trade counts po simbolu i ukupan broj
                    // IBKR koristi "SMART" kao exchange name
                    var exchangeFilter = "SMART";
                    var tradesPerSymbol = await _signalRepo.GetTodayTradeCountsPerSymbolAsync(today, exchangeFilter, ct).ConfigureAwait(false);
                    var tradesTotal = await _signalRepo.GetTodayTradeCountTotalAsync(today, exchangeFilter, ct).ConfigureAwait(false);
                    
                    // Učitaj realizovani PnL
                    var realizedPnlUsd = await _pnlRepo.GetTodayRealizedPnlAsync(today, ct).ConfigureAwait(false);

                    // Restoriraj stanje u DayGuards
                    _dayGuards.RestoreState(tradesPerSymbol, tradesTotal, realizedPnlUsd, now);

                    _log.Information(
                        "[RECOVERY] DayGuards restored: Total={Total} PerSymbol={PerSym} PnL={PnL:F2}",
                        tradesTotal,
                        tradesPerSymbol.Count > 0 
                            ? string.Join(", ", tradesPerSymbol.Select(kvp => $"{kvp.Key}={kvp.Value}"))
                            : "none",
                        realizedPnlUsd);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[RECOVERY] DayGuards recovery failed - continuing with empty state");
                }
            }
            else
            {
                _log.Information("[RECOVERY] DayGuards recovery skipped (missing dependencies)");
            }

            // 3) Pending orders restore (ENTRY only)
            if (_orderRepo is null)
            {
                _log.Information("[RECOVERY] No BrokerOrderRepository, skipping open order restore");
                _log.Information("[RECOVERY] Completed");
                return;
            }

            try
            {
                var open = await _orderRepo.GetOpenOrdersAsync(ct).ConfigureAwait(false);
                _log.Information("[RECOVERY] Found {Count} open broker_orders rows", open.Count);

                // Also get recently filled exit orders (last 24h) to handle late execution details
                var filledExit = await _orderRepo.GetRecentFilledExitOrdersAsync(ct).ConfigureAwait(false);
                _log.Information("[RECOVERY] Found {Count} recently filled exit orders (last 24h)", filledExit.Count);

                var restored = 0;
                var skippedExit = 0;
                var skippedInvalid = 0;

                // Collect exit orders for later registration in IbkrOrderService
                var exitOrdersToRegister = new List<(int brokerOrderId, OrderRequest req, DateTime sentAtUtc)>();

                // Process open orders
                foreach (var o in open)
                {
                    // Skip crypto orders - recovery is only for IBKR orders
                    var exchange = o.Exchange ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(exchange) && 
                        !exchange.Equals("SMART", StringComparison.OrdinalIgnoreCase) &&
                        !exchange.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
                    {
                        // Crypto order - skip (crypto has its own recovery mechanism)
                        continue;
                    }

                    var isExit = !string.IsNullOrWhiteSpace(o.Id) &&
                        o.Id.StartsWith("exit-", StringComparison.OrdinalIgnoreCase);
                    
                    if (isExit)
                    {
                        // Parse brokerOrderId (TWS orderId) - only for IBKR orders
                        if (string.IsNullOrWhiteSpace(o.BrokerOrderId) || 
                            !int.TryParse(o.BrokerOrderId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var exitBrokerOrderId))
                        {
                            skippedExit++;
                            continue;
                        }

                        var exitSym = new Symbol(o.Symbol);
                        var exitSide = string.Equals(o.Side, "buy", StringComparison.OrdinalIgnoreCase)
                            ? OrderSide.Buy
                            : OrderSide.Sell;

                        var exitOt = (o.OrderType ?? "").Trim().ToLowerInvariant();
                        exitOt = exitOt.Replace(" ", "_").Replace("-", "_");
                        if (exitOt == "stp") exitOt = "stop";
                        if (exitOt == "mkt") exitOt = "market";
                        if (exitOt == "stoplimit" || exitOt == "stp_lmt") exitOt = "stop_limit";

                        OrderType exitType;
                        decimal? exitLimit = null;
                        decimal? exitStop = null;

                        if (exitOt == "limit")
                        {
                            if (!o.LimitPrice.HasValue || o.LimitPrice.Value <= 0m) { skippedExit++; continue; }
                            exitType = OrderType.Limit;
                            exitLimit = o.LimitPrice;
                        }
                        else if (exitOt == "stop")
                        {
                            if (!o.StopPrice.HasValue || o.StopPrice.Value <= 0m) { skippedExit++; continue; }
                            exitType = OrderType.Stop;
                            exitStop = o.StopPrice;
                        }
                        else if (exitOt == "stop_limit")
                        {
                            if (!o.StopPrice.HasValue || o.StopPrice.Value <= 0m) { skippedExit++; continue; }
                            exitType = OrderType.Stop;
                            exitStop = o.StopPrice;
                            exitLimit = o.LimitPrice;
                        }
                        else if (exitOt == "market")
                        {
                            exitType = OrderType.Market;
                        }
                        else
                        {
                            skippedExit++;
                            continue;
                        }

                        var exitReq = new OrderRequest(
                            symbol: exitSym,
                            side: exitSide,
                            type: exitType,
                            quantity: o.Qty,
                            limitPrice: exitLimit,
                            tif: TimeInForce.Gtc, // Exit orders are usually GTC
                            correlationId: o.Id,
                            timestampUtc: o.CreatedUtc,
                            ocoGroupId: null,
                            stopPrice: exitStop,
                            isExit: true
                        );

                        exitOrdersToRegister.Add((exitBrokerOrderId, exitReq, o.CreatedUtc));
                        skippedExit++;
                        continue;
                    }

                    var sym = new Symbol(o.Symbol);

                    var side = string.Equals(o.Side, "buy", StringComparison.OrdinalIgnoreCase)
                        ? OrderSide.Buy
                        : OrderSide.Sell;

                    var ot = (o.OrderType ?? "").Trim().ToLowerInvariant();
                    ot = ot.Replace(" ", "_").Replace("-", "_");
                    if (ot == "stp") ot = "stop";
                    if (ot == "mkt") ot = "market";

                    OrderType type;
                    decimal? limit = null;
                    decimal? stop = null;

                    if (ot == "limit")
                    {
                        if (!o.LimitPrice.HasValue || o.LimitPrice.Value <= 0m) { skippedInvalid++; continue; }
                        type = OrderType.Limit;
                        limit = o.LimitPrice;
                    }
                    else if (ot == "stop")
                    {
                        if (!o.StopPrice.HasValue || o.StopPrice.Value <= 0m) { skippedInvalid++; continue; }
                        type = OrderType.Stop;
                        stop = o.StopPrice;
                    }
                    else if (ot == "market")
                    {
                        type = OrderType.Market;
                    }
                    else
                    {
                        skippedInvalid++;
                        continue;
                    }

                    var req = new OrderRequest(
                        symbol: sym,
                        side: side,
                        type: type,
                        quantity: o.Qty,
                        limitPrice: limit,
                        tif: TimeInForce.Day,
                        correlationId: o.Id,
                        timestampUtc: o.CreatedUtc,
                        ocoGroupId: null,
                        stopPrice: stop,
                        isExit: false
                    );

                    // Reserve only for BUY LIMIT
                    var reserved = 0m;
                    if (side == OrderSide.Buy && type == OrderType.Limit && limit is { } lp && lp > 0m)
                    {
                        reserved = o.Qty * lp + _fees.EstimatedPerOrderUsd;
                        SafeReserve(reserved, now, "recovery-open-order");
                    }

                    var po = new PendingOrder(
                        Req: req,
                        ReservedUsd: reserved,
                        AtUtc: o.CreatedUtc,
                        BrokerOrderId: o.BrokerOrderId,
                        LastFeeUsd: 0m,
                        LastExecId: null
                    );

                    _orders.TryAdd(po);
                    restored++;
                    
                    // DEBUG: Log ENTRY order recovery details for mapping analysis
                    if (!string.IsNullOrWhiteSpace(o.BrokerOrderId) && 
                        int.TryParse(o.BrokerOrderId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var entryBrokerOrderId))
                    {
                        _log.Information("[RECOVERY-ENTRY] Restored ENTRY order corr={Corr} brokerOrderId={Bid} sym={Sym} side={Side} qty={Qty} - NOTE: Mapping should be registered in RealIbkrClient",
                            o.Id, entryBrokerOrderId, o.Symbol, o.Side, o.Qty);
                    }
                    else
                    {
                        _log.Warning("[RECOVERY-ENTRY] Restored ENTRY order corr={Corr} brokerOrderId={Bid} sym={Sym} - WARNING: Missing or invalid brokerOrderId, mapping may fail!",
                            o.Id, o.BrokerOrderId ?? "NULL", o.Symbol);
                    }
                    
                    // NOTE: OnOrderPlaced se NE poziva za restore-ovane ordere jer su vec postavljeni ranije
                    // i vec su se brojali u DayGuards kada su prvi put postavljeni
                }

                // DEBUG: Count ENTRY orders with brokerOrderId for mapping analysis
                var entryWithBrokerId = _orders.Snapshot()
                    .Where(po => po.Req != null && !po.Req.IsExit && !string.IsNullOrWhiteSpace(po.BrokerOrderId))
                    .Count();
                var entryWithoutBrokerId = _orders.Snapshot()
                    .Where(po => po.Req != null && !po.Req.IsExit && string.IsNullOrWhiteSpace(po.BrokerOrderId))
                    .Count();
                
                _log.Information("[RECOVERY] Restored={Restored} SkippedExit={SkippedExit} SkippedInvalid={SkippedInvalid} PendingNow={Pending} (DayGuards NOT updated - orders already counted when first placed)",
                    restored, skippedExit, skippedInvalid, _orders.Snapshot().Length);
                _log.Information("[RECOVERY-MAP-ANALYSIS] ENTRY orders with brokerOrderId={WithId} without brokerOrderId={WithoutId} - NOTE: ENTRY orders are NOT registered in RealIbkrClient map (only EXIT orders are registered)",
                    entryWithBrokerId, entryWithoutBrokerId);

                // Process recently filled exit orders (for late execution details)
                foreach (var o in filledExit)
                {
                    // Skip crypto orders - recovery is only for IBKR orders
                    var exchange = o.Exchange ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(exchange) && 
                        !exchange.Equals("SMART", StringComparison.OrdinalIgnoreCase) &&
                        !exchange.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
                    {
                        // Crypto order - skip (crypto has its own recovery mechanism)
                        continue;
                    }

                    // Parse brokerOrderId (TWS orderId) - only for IBKR orders
                    if (string.IsNullOrWhiteSpace(o.BrokerOrderId) || 
                        !int.TryParse(o.BrokerOrderId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var exitBrokerOrderId))
                    {
                        continue;
                    }

                    var exitSym = new Symbol(o.Symbol);
                    var exitSide = string.Equals(o.Side, "buy", StringComparison.OrdinalIgnoreCase)
                        ? OrderSide.Buy
                        : OrderSide.Sell;

                    var exitOt = (o.OrderType ?? "").Trim().ToLowerInvariant();
                    exitOt = exitOt.Replace(" ", "_").Replace("-", "_");
                    if (exitOt == "stp") exitOt = "stop";
                    if (exitOt == "mkt") exitOt = "market";
                    if (exitOt == "stoplimit" || exitOt == "stp_lmt") exitOt = "stop_limit";

                    OrderType exitType;
                    decimal? exitLimit = null;
                    decimal? exitStop = null;

                    if (exitOt == "limit")
                    {
                        if (!o.LimitPrice.HasValue || o.LimitPrice.Value <= 0m) continue;
                        exitType = OrderType.Limit;
                        exitLimit = o.LimitPrice;
                    }
                    else if (exitOt == "stop")
                    {
                        if (!o.StopPrice.HasValue || o.StopPrice.Value <= 0m) continue;
                        exitType = OrderType.Stop;
                        exitStop = o.StopPrice;
                    }
                    else if (exitOt == "stop_limit")
                    {
                        if (!o.StopPrice.HasValue || o.StopPrice.Value <= 0m) continue;
                        exitType = OrderType.Stop;
                        exitStop = o.StopPrice;
                        exitLimit = o.LimitPrice;
                    }
                    else if (exitOt == "market")
                    {
                        exitType = OrderType.Market;
                    }
                    else
                    {
                        continue;
                    }

                    var exitReq = new OrderRequest(
                        symbol: exitSym,
                        side: exitSide,
                        type: exitType,
                        quantity: o.Qty,
                        limitPrice: exitLimit,
                        tif: TimeInForce.Gtc, // Exit orders are usually GTC
                        correlationId: o.Id,
                        timestampUtc: o.CreatedUtc,
                        ocoGroupId: null,
                        stopPrice: exitStop,
                        isExit: true
                    );

                    exitOrdersToRegister.Add((exitBrokerOrderId, exitReq, o.CreatedUtc));
                }

                // Store exit orders for later registration in IbkrOrderService
                if (exitOrdersToRegister.Count > 0)
                {
                    lock (_sync)
                    {
                        _exitOrdersForRegistration = exitOrdersToRegister;
                    }
                    _log.Information("[RECOVERY] Collected {Count} exit orders for registration in IbkrOrderService (open + filled)", exitOrdersToRegister.Count);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[RECOVERY] Failed to restore open orders from broker_orders");
            }

            _log.Information("[RECOVERY] Completed");
        }

        /// <summary>
        /// Upisuje snapshot otvorenih IBKR naloga (OrderRef -> TWS orderId) dobijen preko reqOpenOrders.
        /// Koristi se da recovery mapiranje ne pretpostavlja da je twsId == broker_order_id.
        /// </summary>
        public void SetRecoveredIbkrOpenOrderMap(IReadOnlyDictionary<string, int> twsByCorrelation)
        {
            lock (_sync)
            {
                _recoveredTwsByCorrelation.Clear();
                foreach (var kvp in twsByCorrelation)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value > 0)
                        _recoveredTwsByCorrelation[kvp.Key] = kvp.Value;
                }
            }

            _log.Information("[RECOVERY-MAP] Loaded {Count} IBKR open-order mappings (corr -> twsId)", _recoveredTwsByCorrelation.Count);
        }

        /// <summary>
        /// Registruje exit orders u IbkrOrderService nakon recovery-ja.
        /// Poziva se nakon što se IbkrOrderService kreira.
        /// </summary>
        public void RegisterRecoveredExitOrders()
        {
            if (_orderService is not Denis.TradingEngine.Broker.IBKR.IbkrOrderService ibOrderService)
                return;

            List<(int brokerOrderId, OrderRequest req, DateTime sentAtUtc)>? exitOrders;
            lock (_sync)
            {
                exitOrders = _exitOrdersForRegistration;
                _exitOrdersForRegistration = null; // Clear after registration
            }

            if (exitOrders == null || exitOrders.Count == 0)
                return;

            var registered = 0;
            foreach (var (brokerOrderId, req, sentAtUtc) in exitOrders)
            {
                try
                {
                    int twsOrderId;
                    var hasTwsMapping = false;
                    lock (_sync)
                    {
                        hasTwsMapping = _recoveredTwsByCorrelation.TryGetValue(req.CorrelationId, out twsOrderId);
                    }

                    if (hasTwsMapping)
                    {
                        ibOrderService.RegisterExistingOrder(brokerOrderId, req, sentAtUtc, twsOrderId);
                    }
                    else
                    {
                        // Fallback na stari režim kada reqOpenOrders nije dao OrderRef za ovaj nalog.
                        ibOrderService.RegisterExistingOrder(brokerOrderId, req, sentAtUtc);
                        _log.Warning("[RECOVERY-REGISTER][NO-TWS] Missing tws mapping for corr={Corr} internalId={InternalId} - fallback twsId=internalId",
                            req.CorrelationId, brokerOrderId);
                    }
                    registered++;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[RECOVERY-REGISTER] Failed to register exit order id={Id} corr={Corr}", 
                        brokerOrderId, req.CorrelationId);
                }
            }

            _log.Information("[RECOVERY-REGISTER] Registered {Count} exit orders in IbkrOrderService", registered);
        }

        public async Task ReconcilePendingWithBrokerAsync(IReadOnlyCollection<string> brokerCorrelationIds, CancellationToken ct = default)
        {
            var pending = _orders.Snapshot();
            var now = DateTime.UtcNow;

            if (pending.Length == 0)
            {
                _log.Information("[REC-OPEN] No local pending orders to reconcile.");
                return;
            }

            var brokerSet = brokerCorrelationIds is { Count: > 0 }
                ? new HashSet<string>(brokerCorrelationIds, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _log.Information(
                "[REC-OPEN] Starting reconciliation. BrokerOpen={Broker} LocalPending={Local}",
                brokerSet.Count,
                pending.Length);

            var removed = 0;
            var updatedDb = 0;

            foreach (var po in pending)
            {
                ct.ThrowIfCancellationRequested();

                var corr = po.CorrelationId;

                // Ako broker i dalje vidi ovaj nalog kao open - ne diramo ga.
                if (brokerSet.Contains(corr))
                    continue;

                // Broker NEMA ovaj nalog - tretiramo kao canceled.
                if (_orders.TryRemove(corr, out var removedPo))
                {
                    removed++;

                    if (removedPo is not null && removedPo.ReservedUsd > 0m)
                    {
                        try
                        {
                            _cashService.Unreserve(removedPo.ReservedUsd);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning(
                                ex,
                                "[REC-OPEN] Unreserve failed amount={Amt:F2} corr={Corr}",
                                removedPo.ReservedUsd,
                                corr);
                        }
                    }
                }

                if (_orderRepo is not null)
                {
                    try
                    {
                        await _orderRepo.UpdateStatusAsync(
                            id: corr,
                            status: "canceled",
                            lastMsg: "reconcile-missing-on-broker",
                            ct: ct).ConfigureAwait(false);

                        updatedDb++;
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(
                            ex,
                            "[REC-OPEN] Failed to update broker_orders status for corr={Corr}",
                            corr);
                    }
                }
            }

            _log.Information(
                "[REC-OPEN] Reconcile completed. RemovedPending={Removed} UpdatedDb={UpdatedDb} BrokerOpen={Broker} LocalBefore={Local}",
                removed,
                updatedDb,
                brokerSet.Count,
                pending.Length);
        }

        private void ClearCancelRateLimit(string brokerOrderId)
        {
            if (string.IsNullOrWhiteSpace(brokerOrderId))
                return;

            lock (_sync)
            {
                _lastCancelUtcByBrokerId.Remove(brokerOrderId);
            }
        }
        private readonly Dictionary<string, DateTime> _cancelRequestedUtc = new(StringComparer.OrdinalIgnoreCase);
        private bool TryMarkCancelRequested(string corrId, DateTime nowUtc)
        {
            lock (_sync)
            {
                if (_cancelRequestedUtc.ContainsKey(corrId))
                    return false;

                _cancelRequestedUtc[corrId] = nowUtc;
                return true;
            }
        }
        private void ClearCancelRequested(string corrId)
        {
            lock (_sync)
            {
                _cancelRequestedUtc.Remove(corrId);
            }
        }
        private readonly ConcurrentDictionary<string, DateTime> _terminalByBrokerId = new();
        private bool IsTerminalDuplicate(string? brokerId, DateTime nowUtc, TimeSpan ttl)
        {
            if (string.IsNullOrWhiteSpace(brokerId))
                return false;

            if (_terminalByBrokerId.TryGetValue(brokerId, out var at))
            {
                if (nowUtc - at <= ttl)
                    return true;

                _terminalByBrokerId.TryRemove(brokerId, out _);
            }

            return false;
        }
        private void MarkTerminal(string? brokerId, DateTime nowUtc)
        {
            if (!string.IsNullOrWhiteSpace(brokerId))
                _terminalByBrokerId[brokerId] = nowUtc;
        }
        private (decimal Tp, decimal Sl, decimal? AtrUsed) ComputeTpSlLevels(string symbol, decimal entryPx)
        {
            if (entryPx <= 0m)
                return (0m, 0m, null);

            decimal? atrOpt = null;

            lock (_sync)
            {
                if (_atr.TryGetValue(symbol, out var s) && s.Atr.HasValue && s.Atr.Value > 0m)
                {
                    var rawAtr = s.Atr.Value;

                    // minimalni ATR kao % cene (npr. 0.2% = 0.002m)
                    var minAtrFrac = 0.002m; // TODO: prebaci u config
                    var minAtrAbs = entryPx * minAtrFrac;

                    atrOpt = Math.Max(rawAtr, minAtrAbs);
                }
            }

            var pctTpDist = entryPx * _tpFraction;
            var pctSlDist = entryPx * _slFraction;

            var atrTpDist = atrOpt.HasValue ? TpAtrMultiple * atrOpt.Value : 0m;
            var atrSlDist = atrOpt.HasValue ? SlAtrMultiple * atrOpt.Value : 0m;

            var tpDist = Math.Max(atrTpDist, pctTpDist);
            var slDist = Math.Max(atrSlDist, pctSlDist);

            var tpPx = entryPx + tpDist;
            var slPx = entryPx - slDist;

            return (tpPx, slPx, atrOpt);
        }
        // =========================
        //  SWING MODE helpers
        // =========================
        /// <summary>
        /// Pokušava da otkaže sve EXIT naloge (TP/SL, TIME EXIT) za dati simbol,
        /// kako ne bismo imali dupli SELL (OCO + naš auto-exit).
        /// </summary>
        private void CancelAllExitsForSymbol(string symbol, DateTime nowUtc, string reason)
        {
            // Snapshot je stabilan za iteraciju
            var pendings = _orders.Snapshot();

            foreach (var po in pendings)
            {
                if (po?.Req is null)
                    continue;

                if (!string.Equals(po.Req.Symbol.Ticker, symbol, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Exit naloge prepoznajemo po IsExit ili "exit-" prefixu
                var isExit =
                    po.Req.IsExit ||
                    po.CorrelationId.StartsWith("exit-", StringComparison.OrdinalIgnoreCase);

                if (!isExit)
                    continue;

                // Ako imamo broker order id -> traži cancel na brokeru (ne čistimo lokalno odmah; čisti se na Canceled event)
                if (_orderService is not null && !string.IsNullOrWhiteSpace(po.BrokerOrderId))
                {
                    if (TryMarkCancelRequested(po.CorrelationId, nowUtc))
                    {
                        _log.Information(
                            "[SWING-CANCEL-EXITS] sym={Sym} brokerId={Bid} corr={Corr} reason={Reason}",
                            symbol, po.BrokerOrderId, po.CorrelationId, reason);

                        FireAndForgetCancel(po.BrokerOrderId, symbol);

                        if (_orderRepo is not null)
                        {
                            _ = _orderRepo.UpdateStatusAsync(
                                id: po.CorrelationId,
                                status: "cancel-requested",
                                lastMsg: $"swing-auto-cancel:{reason}",
                                ct: CancellationToken.None);
                        }
                    }

                    continue;
                }

                // Lokalni EXIT bez brokerId (ili nemamo orderService) - kompletan local cleanup
                if (_orders.TryRemove(po.CorrelationId, out var removed) && removed is not null)
                {
                    // 1) unreserve (best-effort)
                    if (removed.ReservedUsd > 0m)
                    {
                        try { _cashService.Unreserve(removed.ReservedUsd); }
                        catch (Exception ex)
                        {
                            _log.Warning(ex, "[SWING-CANCEL-EXITS] unreserve failed sym={Sym} corr={Corr}", symbol, removed.CorrelationId);
                        }
                    }

                    // 2) očisti cum-filled tracking
                    ClearCumFilled(removed.CorrelationId);

                    // 3) ocisti exitPending + cancelRequested marker (da se nista ne "zaglavi")
                    lock (_sync)
                    {
                        _exitPending.Remove(removed.Req.Symbol.Ticker);
                    }
                    ClearCancelRequested(removed.CorrelationId);

                    _log.Information(
                        "[SWING-CANCEL-EXITS] local-only removed sym={Sym} corr={Corr} reason={Reason}",
                        symbol, removed.CorrelationId, reason);

                    // 4) DB status (best-effort)
                    if (_orderRepo is not null)
                    {
                        _ = _orderRepo.MarkCanceledAsync(
                            id: removed.CorrelationId,
                            canceledUtc: nowUtc,
                            ct: CancellationToken.None);
                    }
                }
            }
        }
        /// <summary>
        /// Auto-exit logika zasnovana na swing configu.
        /// Radi SAMO ako je AutoExitReal=true i imamo real order service.
        /// </summary>
        /// 

        private async Task<bool> IsExternalSwingSymbolAsync(string symbol, CancellationToken ct)
        {
            if (_swingPosRepo is null)
                return false;

            try
            {
                var row = await _swingPosRepo.GetOpenBySymbolAsync(symbol, "SMART", CancellationToken.None).ConfigureAwait(false);

                if (row is null || !row.IsOpen)
                    return false;

                var strategy = row.Strategy ?? string.Empty;
                return strategy.StartsWith("External/", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[SWING-EXT] IsExternalSwingSymbol failed sym={Sym}", symbol);
                // fail-safe: radije NE blokiramo exit ako ne možemo da pročitamo DB
                return false;
            }
        }
        private async Task EvaluateSwingAutoExitsAsync(DateTime nowUtc)
        {
            if (_orderService is null) return;                  // radi samo u REAL
            if (_swingConfig is null) return;
            if (_swingConfig.Mode != SwingMode.Swing) return;   // samo SWING scope
            if (!_swingConfig.AutoExitReal) return;             // global OFF

            var positions = _positionBook.Snapshot();
            var live = positions.Where(p => p.Quantity != 0m).ToArray();
            if (live.Length == 0) return;

            foreach (var p in live)
            {


                var sym = p.Symbol;
                var qty = p.Quantity;
                if (qty <= 0m)
                    continue;

                // NOVO: SKIP External/IBKR swing pozicije
                if (await IsExternalSwingSymbolAsync(sym, CancellationToken.None).ConfigureAwait(false))
                {
                    _log.Debug("[SWING-AUTO-EXIT] skip external sym={Sym}", sym);
                    continue;
                }

                // NOVO: zastita za External/IBKR pozicije
                if (IsExternalPosition(sym))
                {
                    _log.Debug(
                        "[SWING-AUTO-EXIT-SKIP] sym={Sym} qty={Qty:F4} marked as EXTERNAL/IBKR ?? no auto-exit",
                        sym,
                        qty);
                    continue;
                }

                PositionRuntimeState? rt;
                lock (_sync)
                {
                    _posRuntime.TryGetValue(sym, out rt);
                }

                if (rt is null)
                    continue;

                var ageDays = (nowUtc - rt.EntryUtc).TotalDays;

                // ---- 1) MaxHoldingDays auto-exit ----
                var triggerMaxHolding =
                    _swingConfig.AutoExitOnMaxHoldingDays &&
                    _swingConfig.MaxHoldingDays > 0 &&
                    ageDays >= _swingConfig.MaxHoldingDays;

                // ---- 2) Weekend auto-exit (UTC DOSLEDNO, jer config je WeekendCutoffUtc) ----
                var triggerWeekend =
                    _swingConfig.AutoExitBeforeWeekend &&
                    _swingConfig.CloseBeforeWeekend &&
                    nowUtc.DayOfWeek == DayOfWeek.Friday &&
                    nowUtc.TimeOfDay >= _swingConfig.WeekendCutoffUtc;

                if (!triggerMaxHolding && !triggerWeekend)
                    continue;

                // svež quote (NBBO + maxAge + spread guard)
                if (!TryGetQuote(sym, nowUtc, out var q2, out var liqReason))
                {
                    _log.Warning(
                        "[SWING-AUTO-EXIT-SKIP] {Sym} no quote (reason={Reason}) ageDays={Age:F2}",
                        sym, liqReason ?? "n/a", ageDays);
                    continue;
                }

                var refPx = q2.Bid ?? q2.Last ?? q2.Ask ?? 0m;
                if (refPx <= 0m)
                {
                    _log.Warning(
                        "[SWING-AUTO-EXIT-SKIP] {Sym} invalid refPx ageDays={Age:F2}",
                        sym, ageDays);
                    continue;
                }

                var reasonTag = string.IsNullOrWhiteSpace(_swingConfig.AutoExitReasonTag)
                    ? "SWING-AUTO"
                    : _swingConfig.AutoExitReasonTag;

                string reason;
                if (triggerMaxHolding && triggerWeekend)
                    reason = $"{reasonTag} MAX-HOLDING+WEEKEND age={ageDays:F2}d";
                else if (triggerMaxHolding)
                    reason = $"{reasonTag} MAX-HOLDING age={ageDays:F2}d";
                else
                    reason = $"{reasonTag} WEEKEND-CUTOFF age={ageDays:F2}d";

                _log.Warning(
                    "[SWING-AUTO-EXIT] sym={Sym} qty={Qty:F4} refPx={Px:F2} reason={Reason}",
                    sym, qty, refPx, reason);

                // odredi prefix za correlationId
                string corrPrefix;
                if (triggerMaxHolding && triggerWeekend)
                {
                    // ako ti je draže, možeš da tretiraš kao weekend ili kao maxDays;
                    // ovde stavljam kombinovani prefix, helper ce ga mapirati na Weekend.
                    corrPrefix = "exit-auto-maxdays-weekend";
                }
                else if (triggerMaxHolding)
                {
                    corrPrefix = "exit-auto-maxdays";
                }
                else
                {
                    corrPrefix = "exit-auto-weekend";
                }

                // 1) otkazi postojece exit naloge
                CancelAllExitsForSymbol(sym, nowUtc, reason);

                // 2) pošalji novi exit nalog sa jasnim prefixom
                SendExit(sym, qty, refPx, reason, corrPrefix);
            }
        }

        private bool IsDuplicateRealFill(string corrId, decimal sliceQty, decimal fillPx, DateTime utcNow)
        {
            // ključ definisan kao (corrId | qty | price)
            var key = $"{corrId}|{sliceQty:F8}|{fillPx:F8}";

            lock (_sync)
            {
                // ako smo vec obradili isti kljuc u poslednjih ProcessedFillTtl -> duplikat
                if (_processedRealFills.TryGetValue(key, out var at))
                {
                    if (utcNow - at <= ProcessedFillTtl)
                        return true;
                }

                // povremeni cleanup da ne raste beskonačno
                if (_processedRealFills.Count > 10_000)
                {
                    var threshold = utcNow - ProcessedFillTtl;
                    var toRemove = _processedRealFills
                        .Where(kvp => kvp.Value < threshold)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var k in toRemove)
                        _processedRealFills.Remove(k);
                }

                _processedRealFills[key] = utcNow;
                return false;
            }
        }

        private bool IsExternalPosition(string symbol)
        {
            lock (_sync)
            {
                return _posRuntime.TryGetValue(symbol, out var rt) && rt.IsExternal;
            }
        }
        // =========================
        //  DISPOSE
        // =========================
        public void Dispose()
        {
            try
            { _cashRefreshCts.Cancel();
            }
            catch
            { //ignore
            }

            try
            { _feed.MarketQuoteUpdated -= _strategy.OnQuote;
            }
            catch
            { //ignore
            }

            try
            { _feed.MarketQuoteUpdated -= EvaluatePaperExitsOnQuote;
            }
            catch
            { //ignore
            }

            try
            { if (_paperForwarder is not null) _feed.MarketQuoteUpdated -= _paperForwarder;
            }
            catch
            { //ignore
            }

            try
            { _strategy.TradeSignalGenerated -= OnTradeSignal;
            }
            catch
            { //ignore
            }

            try
            { _paperSim.Filled -= OnPaperFilled;
            }
            catch
            { //ignore
            }

            try
            { _feed.MarketQuoteUpdated -= OnQuoteCached;
            }
            catch
            { //ignore
            }

            try
            { _feed.MarketQuoteUpdated -= UpdateAtrOnQuote;
            }
            catch
            { //ignore
            }
        }
    }
}




