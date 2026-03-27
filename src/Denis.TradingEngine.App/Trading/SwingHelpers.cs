using Denis.TradingEngine.App.Config;
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Core.Swing;
using Denis.TradingEngine.Logging;
using Serilog;
using System;

namespace Denis.TradingEngine.App.Trading
{
    /// <summary>
    /// Pomoćne funkcije za swing / intraday odluke.
    /// Za sada SAMO utility-jevi, ne menjaju ponašanje sistema.
    /// </summary>
    public sealed class SwingHelpers
    {
        private static readonly ILogger Log = AppLog.ForContext<SwingHelpers>();

        public static bool IsSwingMode(SwingTradingConfig cfg)
        {
            var result = cfg.Mode == SwingMode.Swing;
            Log.Debug("[SWING-HELPER] IsSwingMode Mode={Mode} => {Result}", cfg.Mode, result);
            return result;
        }

        public static bool IsIntradayOnly(SwingTradingConfig cfg)
        {
            var result = cfg.Mode == SwingMode.IntradayOnly;
            Log.Debug("[SWING-HELPER] IsIntradayOnly Mode={Mode} => {Result}", cfg.Mode, result);
            return result;
        }

        public static bool IsSwingOff(SwingTradingConfig cfg)
        {
            var result = cfg.Mode == SwingMode.Off;
            Log.Debug("[SWING-HELPER] IsSwingOff Mode={Mode} => {Result}", cfg.Mode, result);
            return result;
        }

        /// <summary>
        /// Da li smo u zoni gde treba da razmišljamo o zatvaranju
        /// pozicija zbog vikenda (petak posle određenog cut-off vremena).
        /// Ne radi ništa automatski – samo daje odgovor.
        /// </summary>
        public static bool ShouldProtectWeekend(DateTimeOffset nowUtc, SwingTradingConfig cfg)
        {
            if (!cfg.CloseBeforeWeekend)
            {
                Log.Debug("[SWING-HELPER] Weekend protection OFF (CloseBeforeWeekend=false)");
                return false;
            }

            if (nowUtc.DayOfWeek != DayOfWeek.Friday)
            {
                Log.Debug("[SWING-HELPER] Not Friday, skip weekend protection. nowUtc={NowUtc}", nowUtc);
                return false;
            }

            var cutoff = cfg.WeekendCutoffUtc; // pretpostavka: TimeSpan
            var should = nowUtc.TimeOfDay >= cutoff;

            Log.Information(
                "[SWING-HELPER] Weekend check: nowUtc={NowUtc} cutoffUtc={Cutoff} shouldProtect={Should}",
                nowUtc, cutoff, should
            );

            return should;
        }



        public static (SwingExitReason? ExitReason, bool AutoExit) InferSwingExitReason(OrderRequest req)
        {
            var corr = req.CorrelationId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(corr))
                return (null, false);

            // 1) OCO TP / SL – nisu AUTO-EXIT, nego normalni SL/TP
            if (corr.StartsWith("exit-tp-", StringComparison.OrdinalIgnoreCase))
                return (SwingExitReason.TakeProfit, false);

            if (corr.StartsWith("exit-sl-", StringComparison.OrdinalIgnoreCase))
                return (SwingExitReason.StopLoss, false);

            // 2) SWING auto-exit (REAL + AutoExitReal=true)
            if (corr.StartsWith("exit-swing-max-weekend-", StringComparison.OrdinalIgnoreCase))
                return (SwingExitReason.SwingMaxDays, true);   // dominantan razlog: max days

            if (corr.StartsWith("exit-swing-max-", StringComparison.OrdinalIgnoreCase))
                return (SwingExitReason.SwingMaxDays, true);

            if (corr.StartsWith("exit-swing-weekend-", StringComparison.OrdinalIgnoreCase))
                return (SwingExitReason.SwingWeekend, true);

            if (corr.StartsWith("exit-swing-protect-", StringComparison.OrdinalIgnoreCase))
                return (SwingExitReason.TrailExit, true);

            if (corr.StartsWith("exit-swing-", StringComparison.OrdinalIgnoreCase))
                return (SwingExitReason.SwingMaxDays, true);   // fallback, ne bi trebalo da se desi

            // 3) Svi ostali exit-* tretiramo kao manual/other
            if (corr.StartsWith("exit-", StringComparison.OrdinalIgnoreCase))
                return (SwingExitReason.Manual, false);

            // 4) ako uopšte nije exit-* (ne bi trebalo za close), nemamo posebnu semantiku
            return (null, false);
        }


    }
}
