#nullable enable
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.MetricsServer;
using Denis.TradingEngine.Core.Trading;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;

namespace Denis.TradingEngine.Strategy.Filters
{
    [Flags]
    public enum SignalBlockReason
    {
        None = 0,
        AtrTooSmall = 1 << 0,
        AtrTooBig = 1 << 1,
        SpreadTooWide = 1 << 2,
        ActivityTooLow = 1 << 3,
        SymbolDailyCapHit = 1 << 4,
        MicroFilterRejected = 1 << 5,
        RejectionSpeed = 1 << 6,
        OpenFakeBreakout = 1 << 7
    }

    /// <summary>
    /// Stable string reason codes for metrics and reporting.
    /// These codes are used in Prometheus metrics and should never change.
    /// </summary>
    public static class SignalBlockReasonCode
    {
        public const string ATR_TOO_LOW = "atr_too_low";
        public const string ATR_TOO_HIGH = "atr_too_high";
        public const string SPREAD_TOO_WIDE = "spread_too_wide";
        public const string TICKS_TOO_LOW = "ticks_too_low";
        public const string CAP_REACHED = "cap_reached";
        public const string MICRO_FILTER_REJECTED = "micro_filter_rejected";
        public const string REJECTION_SPEED = "rejection_speed";
        public const string OPEN_FAKE_BREAKOUT = "open_fake_breakout";
        public const string ACCEPTED = "accepted"; // For tracking accepts too
    }

    public sealed class SignalSlayerConfig
    {
        public bool EnableMicroFilter { get; init; } = true;
        
        // Opcioni override parametri (null = koristi default vrednosti za IBKR kompatibilnost)
        public decimal? MinAtrFractionOfPrice { get; init; } = null; // default: 0.00001m
        public decimal? MaxAtrFractionOfPrice { get; init; } = null; // default: 0.05m
        public decimal? MaxSpreadBps { get; init; } = null; // default: 30m
        public int? MinActivityTicks { get; init; } = null; // default: 10
        public int? MaxSignalsPerSymbolPerDay { get; init; } = null; // default: 15
        
        // Distribution Protection (anti-manufactured-spike filters)
        public DistributionProtectionConfig? DistributionProtection { get; init; } = null;
    }

    public sealed record SignalSlayerDecision(
        bool Accepted,
        SignalBlockReason Reasons,
        string? ReasonText
    );

    /// <summary>
    /// Kontekst: sve što slayer treba da zna.
    /// RTH NE postoji ovde — to rešava orchestrator.
    /// </summary>
    public sealed record SignalSlayerContext(
        string Symbol,
        decimal Price,
        decimal? Atr,
        decimal? SpreadBps,
        int ActivityTicks,
        DateTime UtcNow,
        string StrategyName,
        string Regime,
        decimal? Slope5,
        decimal? Slope20,
        decimal AtrFractionOfPrice,
        string? Exchange = null,
        // Distribution Protection context (optional)
        decimal? MovePctFromEntry = null,  // Price move percentage from entry (for time-of-day filter)
        bool? HasValidPullbackStructure = null  // Whether valid pullback structure exists (for time-of-day filter)
    );

    /// <summary>
    /// SignalSlayer v1 – čisti filter kvaliteta signala (hard reject).
    /// NEMA vremenskih pravila, NEMA RTH – to radi orchestrator.
    /// </summary>
    public sealed class SignalSlayer
    {
        private readonly object _sync = new();

        // ATR sanity (config-based sa fallback na default za IBKR kompatibilnost)
        private readonly decimal _minAtrFrac;
        private readonly decimal _maxAtrFrac;

        // Spread (config-based sa fallback na default za IBKR kompatibilnost)
        private readonly decimal _maxSpreadBps;

        // Aktivnost (config-based sa fallback na default za IBKR kompatibilnost)
        private readonly int _minActivityTicks;

        // Dnevni limit po simbolu (config-based sa fallback na default za IBKR kompatibilnost)
        private readonly int _maxSignalsPerSymbolPerDay;

        // Interne statistike za per-day cap
        private readonly Dictionary<(string Symbol, DateTime Day), int> _acceptedCounts = new();

        // Optional DB repository for persistence (non-blocking)
        private readonly SignalSlayerDecisionRepository? _dbRepo;
        private readonly string? _runEnv; // Paper / Real

        // Per-symbol micro-filter config provider (legacy fallback, null = use default)
        private readonly Func<string, MicroSignalFilterConfig>? _microFilterConfigProvider;
        // Preferred provider with full signal context (symbol + exchange + regime...)
        private readonly Func<SignalSlayerContext, MicroSignalFilterConfig>? _microFilterContextConfigProvider;
        private readonly MicroSignalFilterConfig _defaultMicroFilterConfig;

        private readonly SignalSlayerConfig _cfg;
        
        public SignalSlayer(
            SignalSlayerConfig cfg,
            SignalSlayerDecisionRepository? dbRepo = null,
            string? runEnv = null,
            Func<string, MicroSignalFilterConfig>? microFilterConfigProvider = null,
            Func<SignalSlayerContext, MicroSignalFilterConfig>? microFilterContextConfigProvider = null)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _dbRepo = dbRepo;
            _runEnv = runEnv;
            _microFilterConfigProvider = microFilterConfigProvider;
            _microFilterContextConfigProvider = microFilterContextConfigProvider;

            // Config-based parametri sa fallback na default vrednosti (za IBKR kompatibilnost)
            _minAtrFrac = _cfg.MinAtrFractionOfPrice ?? 0.00001m; // default: 0.001%
            _maxAtrFrac = _cfg.MaxAtrFractionOfPrice ?? 0.05m;    // default: 5%
            _maxSpreadBps = _cfg.MaxSpreadBps ?? 30m;             // default: 30 bps
            _minActivityTicks = _cfg.MinActivityTicks ?? 10;        // default: 10 ticks
            _maxSignalsPerSymbolPerDay = _cfg.MaxSignalsPerSymbolPerDay ?? 15; // default: 15

            // Default micro-filter config (fallback) – MinAtrFractionOfPrice usklađen sa Slayer sanity (0.00001) da nema duplog praga
            _defaultMicroFilterConfig = new MicroSignalFilterConfig
            {
                Enabled = true,
                MinSlope20Bps = -0.80m,
                MinAtrFractionOfPrice = 0.00001m,     // isti prag kao Slayer _minAtrFrac (0.00001) – jedan izvor istine
                MaxSpreadBps = 22.0m,
                MinTicksPerWindow = 55
            };
        }

        private MicroSignalFilter GetMicroFilter(SignalSlayerContext ctx)
        {
            var config =
                _microFilterContextConfigProvider?.Invoke(ctx)
                ?? _microFilterConfigProvider?.Invoke(ctx.Symbol)
                ?? _defaultMicroFilterConfig;
            return new MicroSignalFilter(config);
        }

        public SignalSlayerDecision ShouldAccept(SignalSlayerContext ctx)
        {
            // --------- KONTEKST LOG ---------

            var slope20Bps = (ctx.Slope20.HasValue && ctx.Price > 0m) ? (ctx.Slope20.Value / ctx.Price) * 10000m : 0m;
            Log.Information(
                "[SLAYER-CTX] {Sym} px={Px} atr={Atr} atrFrac={AtrFrac} spread={Spread}bps ticks={Ticks} slope5={Slope5} slope20={Slope20} regime={Regime} strat={Strat}  slope20bps={Slope20Bps:F3},",
                ctx.Symbol,
                ctx.Price,
                ctx.Atr ?? 0m,
                ctx.AtrFractionOfPrice,
                ctx.SpreadBps ?? 0m,
                ctx.ActivityTicks,
                ctx.Slope5,
                ctx.Slope20,
                ctx.Regime,
                ctx.StrategyName,
                slope20Bps    
            );

            var reasons = SignalBlockReason.None;

            // 0) Micro-filter – samo ako je uključen u configu
            if (_cfg.EnableMicroFilter)
            {
                var microFilter = GetMicroFilter(ctx);
                var microInput = new MicroSignalFilterInput
                {
                    Symbol = ctx.Symbol,
                    Price = ctx.Price,
                    // ako hoćeš da budeš safe kad slope nije još spreman:
                    Slope5 = ctx.Slope5 ,
                    Slope20 = ctx.Slope20 ,
                    AtrFractionOfPrice = ctx.AtrFractionOfPrice,
                    SpreadBps = ctx.SpreadBps ?? 0m,
                    TicksPerWindow = ctx.ActivityTicks,
                    Regime = ctx.Regime,
                    UtcNow = ctx.UtcNow
                };

                var micro = microFilter.Evaluate(microInput);

                if (!micro.Accepted)
                {
                    reasons |= SignalBlockReason.MicroFilterRejected;

                    if (!string.IsNullOrWhiteSpace(micro.Reason) &&
                        micro.Reason.Contains("range-slope-gate", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Information(
                            "[SLAYER-MICRO-RANGE] {Sym} RANGE-SLOPE-BLOCK regime={Regime} slope20bps={Slope20Bps:F3} floor={Floor:F3}",
                            ctx.Symbol,
                            ctx.Regime,
                            slope20Bps,
                            -0.07m
                        );
                    }

                    Log.Information(
                        "[SLAYER-MICRO] {Sym} HARD-REJECT reason={Reason} px={Px} atrFrac={AtrFrac} slope5={Slope5} slope20={Slope20} spread={Spread}bps ticks={Ticks} regime={Regime}  slope20bps={Slope20Bps:F3}",
                        ctx.Symbol,
                        micro.Reason,
                        ctx.Price,
                        ctx.AtrFractionOfPrice,
                        ctx.Slope5,
                        ctx.Slope20,
                        ctx.SpreadBps ?? 0m,
                        ctx.ActivityTicks,
                        ctx.Regime,
                        slope20Bps
                    );
                }
                else
                {
                    Log.Information(
                        "[SLAYER-MICRO] {Sym} OK px={Px} atrFrac={AtrFrac} slope5={Slope5} slope20={Slope20} spread={Spread}bps ticks={Ticks} regime={Regime}",
                        ctx.Symbol,
                        ctx.Price,
                        ctx.AtrFractionOfPrice,
                        ctx.Slope5,
                        ctx.Slope20,
                        ctx.SpreadBps ?? 0m,
                        ctx.ActivityTicks,
                        ctx.Regime
                    );
                }
            }
            else
            {
                Log.Information(
                    "[SLAYER-MICRO] {Sym} DISABLED px={Px} atrFrac={AtrFrac} spread={Spread}bps ticks={Ticks} regime={Regime}",
                    ctx.Symbol,
                    ctx.Price,
                    ctx.AtrFractionOfPrice,
                    ctx.SpreadBps ?? 0m,
                    ctx.ActivityTicks,
                    ctx.Regime
                );
            }

            // 0.5) Distribution Protection filters (anti-manufactured-spike)
            var distProtection = _cfg.DistributionProtection;
            if (distProtection != null && (distProtection.Enabled || distProtection.LogWhenDisabled))
            {
                // Time-of-day hard rule (open trap detection)
                if (distProtection.TimeOfDay.Enabled || distProtection.LogWhenDisabled)
                {
                    var timeOfDayResult = CheckTimeOfDayRule(ctx, distProtection.TimeOfDay, distProtection.Enabled && distProtection.LogWhenDisabled);
                    if (timeOfDayResult.ShouldReject)
                    {
                        if (distProtection.Enabled && distProtection.TimeOfDay.Enabled)
                        {
                            reasons |= SignalBlockReason.OpenFakeBreakout;
                        }
                        
                        if (distProtection.LogWhenDisabled || (distProtection.Enabled && distProtection.TimeOfDay.Enabled))
                        {
                            Log.Information(
                                "[DIST-PROT] {Action} OPEN_FAKE_BREAKOUT {Sym} minutesFromOpen={Min} movePct={Move:F4} hasValidPB={HasPB} px={Px}",
                                (distProtection.Enabled && distProtection.TimeOfDay.Enabled) ? "REJECTING" : "Would reject (DISABLED)",
                                ctx.Symbol,
                                timeOfDayResult.MinutesFromOpen,
                                ctx.MovePctFromEntry ?? 0m,
                                ctx.HasValidPullbackStructure ?? false,
                                ctx.Price);
                        }
                    }
                }
                
                // Rejection Speed filter (pre-entry check - would need state tracking, skip for now)
                // NOTE: Rejection Speed filter requires state tracking (max price after breakout),
                // so it's better implemented as early exit guard in TradingOrchestrator, not here.
            }

            // 1) ATR sanity
            if (ctx.Atr.HasValue && ctx.Price > 0m)
            {
                var atrFrac = ctx.Atr.Value / ctx.Price;

                if (atrFrac < _minAtrFrac)
                    reasons |= SignalBlockReason.AtrTooSmall;

                if (atrFrac > _maxAtrFrac)
                    reasons |= SignalBlockReason.AtrTooBig;
            }

            // 2) Spread
            if (ctx.SpreadBps.HasValue && ctx.SpreadBps.Value > _maxSpreadBps)
                reasons |= SignalBlockReason.SpreadTooWide;

            // 3) Aktivnost
            if (ctx.ActivityTicks < _minActivityTicks)
                reasons |= SignalBlockReason.ActivityTooLow;

            // 4) Daily cap — samo ako je sve ostalo prošlo
            if (reasons == SignalBlockReason.None)
            {
                lock (_sync)
                {
                    PruneOldDays_NoLock(ctx.UtcNow);

                    var key = (ctx.Symbol, ctx.UtcNow.Date);
                    if (_acceptedCounts.TryGetValue(key, out var count))
                    {
                        if (count >= _maxSignalsPerSymbolPerDay)
                            reasons |= SignalBlockReason.SymbolDailyCapHit;
                    }
                }
            }

            // REJECT
            if (reasons != SignalBlockReason.None)
            {
                var reasonTxt = BuildReasonText(ctx, reasons);
                var reasonCodes = ExtractReasonCodes(reasons);

                // Emit Prometheus metrics for each rejection reason
                foreach (var code in reasonCodes)
                {
                    try
                    {
                        // Legacy metrika (bez phase)
                        StrategyMetrics.Instance.SignalSlayerDecision(ctx.StrategyName, ctx.Symbol, code);
                        // Phase-aware metrika
                        StrategyMetrics.Instance.SignalSlayerRejectedByPhase(
                            ctx.StrategyName,
                            ctx.Symbol,
                            code,
                            ctx.UtcNow
                        );
                    }
                    catch
                    {
                        // Metrics should never crash the strategy
                    }
                }

                // Persist to DB (non-blocking, fire-and-forget)
                if (_dbRepo is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Log each rejection reason separately
                            foreach (var code in reasonCodes)
                            {
                                await _dbRepo.InsertAsync(
                                    utc: ctx.UtcNow,
                                    symbol: ctx.Symbol,
                                    strategy: ctx.StrategyName,
                                    accepted: false,
                                    reasonCode: code,
                                    price: ctx.Price,
                                    atr: ctx.Atr,
                                    atrFraction: ctx.AtrFractionOfPrice,
                                    spreadBps: ctx.SpreadBps,
                                    activityTicks: ctx.ActivityTicks,
                                    regime: ctx.Regime,
                                    slope5: ctx.Slope5,
                                    slope20: ctx.Slope20,
                                    runEnv: _runEnv,
                                    exchange: ctx.Exchange
                                );
                            }
                        }
                        catch
                        {
                            // DB errors should never crash the strategy
                        }
                    });
                }

                Log.Information(
                    "[SLAYER-REJECT] {Sym} REJECT reasons={Reasons} codes={Codes} msg={Msg}",
                    ctx.Symbol,
                    reasons,
                    string.Join(",", reasonCodes),
                    reasonTxt
                );

                return new SignalSlayerDecision(
                    Accepted: false,
                    Reasons: reasons,
                    ReasonText: reasonTxt
                );
            }

            // ACCEPT
            int newCount;
            lock (_sync)
            {
                var key = (ctx.Symbol, ctx.UtcNow.Date);
                if (_acceptedCounts.TryGetValue(key, out var count))
                    _acceptedCounts[key] = newCount = count + 1;
                else
                    _acceptedCounts[key] = newCount = 1;
            }

            // Emit Prometheus metric for acceptance
            try
            {
                // Legacy metrika (bez phase)
                StrategyMetrics.Instance.SignalSlayerDecision(ctx.StrategyName, ctx.Symbol, SignalBlockReasonCode.ACCEPTED);
                // Phase-aware metrika (accepted se ne loguje kao rejection, ali možemo da dodamo ako treba)
            }
            catch
            {
                // Metrics should never crash the strategy
            }

            // Persist to DB (non-blocking, fire-and-forget)
            if (_dbRepo is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _dbRepo.InsertAsync(
                            utc: ctx.UtcNow,
                            symbol: ctx.Symbol,
                            strategy: ctx.StrategyName,
                            accepted: true,
                            reasonCode: SignalBlockReasonCode.ACCEPTED,
                            price: ctx.Price,
                            atr: ctx.Atr,
                            atrFraction: ctx.AtrFractionOfPrice,
                            spreadBps: ctx.SpreadBps,
                            activityTicks: ctx.ActivityTicks,
                            regime: ctx.Regime,
                            slope5: ctx.Slope5,
                            slope20: ctx.Slope20,
                            runEnv: _runEnv,
                            exchange: ctx.Exchange
                        );
                    }
                    catch
                    {
                        // DB errors should never crash the strategy
                    }
                });
            }

            Log.Information(
                "[SLAYER-ACCEPT] {Sym} ACCEPT px={Px} atr={Atr} atrFrac={AtrFrac} spread={Spread}bps ticks={Ticks} slope5={Slope5} slope20={Slope20} regime={Regime} dayCount={DayCount}",
                ctx.Symbol,
                ctx.Price,
                ctx.Atr ?? 0m,
                ctx.AtrFractionOfPrice,
                ctx.SpreadBps ?? 0m,
                ctx.ActivityTicks,
                ctx.Slope5,
                ctx.Slope20,
                ctx.Regime,
                newCount
            );

            return new SignalSlayerDecision(true, SignalBlockReason.None, null);
        }
        private void PruneOldDays_NoLock(DateTime now)
        {
            var minDay = now.Date.AddDays(-5);
            var oldKeys = _acceptedCounts
                .Where(kvp => kvp.Key.Day < minDay)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in oldKeys)
                _acceptedCounts.Remove(key);
        }
        /// <summary>
        /// Extracts stable string reason codes from enum flags.
        /// Returns list of codes for metrics tracking.
        /// </summary>
        private static List<string> ExtractReasonCodes(SignalBlockReason reasons)
        {
            var codes = new List<string>();

            if (reasons.HasFlag(SignalBlockReason.AtrTooSmall))
                codes.Add(SignalBlockReasonCode.ATR_TOO_LOW);

            if (reasons.HasFlag(SignalBlockReason.AtrTooBig))
                codes.Add(SignalBlockReasonCode.ATR_TOO_HIGH);

            if (reasons.HasFlag(SignalBlockReason.SpreadTooWide))
                codes.Add(SignalBlockReasonCode.SPREAD_TOO_WIDE);

            if (reasons.HasFlag(SignalBlockReason.ActivityTooLow))
                codes.Add(SignalBlockReasonCode.TICKS_TOO_LOW);

            if (reasons.HasFlag(SignalBlockReason.SymbolDailyCapHit))
                codes.Add(SignalBlockReasonCode.CAP_REACHED);

            if (reasons.HasFlag(SignalBlockReason.MicroFilterRejected))
                codes.Add(SignalBlockReasonCode.MICRO_FILTER_REJECTED);

            if (reasons.HasFlag(SignalBlockReason.RejectionSpeed))
                codes.Add(SignalBlockReasonCode.REJECTION_SPEED);

            if (reasons.HasFlag(SignalBlockReason.OpenFakeBreakout))
                codes.Add(SignalBlockReasonCode.OPEN_FAKE_BREAKOUT);

            return codes;
        }

        private static string BuildReasonText(SignalSlayerContext ctx, SignalBlockReason reasons)
        {
            var parts = new List<string>();

            if (reasons.HasFlag(SignalBlockReason.AtrTooSmall))
                parts.Add("ATR too small");

            if (reasons.HasFlag(SignalBlockReason.AtrTooBig))
                parts.Add("ATR too big");

            if (reasons.HasFlag(SignalBlockReason.SpreadTooWide))
                parts.Add($"spread too wide ({(ctx.SpreadBps ?? 0m):F1} bps)");

            if (reasons.HasFlag(SignalBlockReason.ActivityTooLow))
                parts.Add($"activity too low (ticks={ctx.ActivityTicks})");

            if (reasons.HasFlag(SignalBlockReason.SymbolDailyCapHit))
                parts.Add("symbol daily cap hit");

            if (reasons.HasFlag(SignalBlockReason.MicroFilterRejected))
                parts.Add("micro-filter rejected (PA / micro-structure)");

            if (reasons.HasFlag(SignalBlockReason.RejectionSpeed))
                parts.Add("rejection speed (rapid drop after local max)");

            if (reasons.HasFlag(SignalBlockReason.OpenFakeBreakout))
                parts.Add("open fake breakout (early breakout without valid pullback structure)");

            return
                $"Signal blocked for {ctx.Symbol}: {string.Join(", ", parts)} " +
                $"price={ctx.Price:F4} atr={(ctx.Atr ?? 0m):F6} spread={(ctx.SpreadBps ?? 0m):F1}bps " +
                $"ticks={ctx.ActivityTicks} regime={ctx.Regime} strat={ctx.StrategyName}";
        }

        /// <summary>
        /// Checks Time-of-day hard rule (open trap detection).
        /// </summary>
        private (bool ShouldReject, int MinutesFromOpen) CheckTimeOfDayRule(
            SignalSlayerContext ctx,
            TimeOfDayConfig config,
            bool isEnabled)
        {
            // Get trading phase
            var phase = TradingPhase.GetPhase(ctx.UtcNow);
            
            // Only check in open_1h phase
            if (phase != TradingPhase.Phase.Open1H)
                return (false, 0);

            // Calculate minutes from RTH open (09:30 ET)
            var minutesFromOpen = GetMinutesFromRthOpen(ctx.UtcNow);
            
            // Check if too early (before MaxMinutesFromOpen)
            if (minutesFromOpen > config.MaxMinutesFromOpen)
                return (false, minutesFromOpen);

            // Check if move is large enough
            var movePct = ctx.MovePctFromEntry ?? 0m;
            if (movePct < config.MinMovePct)
                return (false, minutesFromOpen);

            // Check if valid pullback structure is required
            if (config.RequireValidPullback)
            {
                var hasValidPB = ctx.HasValidPullbackStructure ?? false;
                if (!hasValidPB)
                {
                    // All conditions met: early + large move + no valid PB structure
                    return (true, minutesFromOpen);
                }
            }
            else
            {
                // If valid PB is not required, reject if early + large move
                return (true, minutesFromOpen);
            }

            return (false, minutesFromOpen);
        }

        /// <summary>
        /// Calculates minutes from RTH open (09:30 ET).
        /// </summary>
        private static int GetMinutesFromRthOpen(DateTime utcNow)
        {
            // Convert to NY time
            TimeZoneInfo nyTz;
            try
            {
                nyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            }
            catch
            {
                nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }

            var nyLocal = TimeZoneInfo.ConvertTimeFromUtc(utcNow, nyTz);
            var rthOpen = nyLocal.Date.AddHours(9).AddMinutes(30); // 09:30 ET
            
            if (nyLocal < rthOpen)
                return 0; // Before RTH open

            var diff = nyLocal - rthOpen;
            return (int)diff.TotalMinutes;
        }
    }
}
