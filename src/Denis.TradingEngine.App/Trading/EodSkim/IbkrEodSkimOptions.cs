#nullable enable
using System.Collections.Generic;

namespace Denis.TradingEngine.App.Trading.EodSkim
{
    /// <summary>
    /// Konfiguracija za IBKR end-of-day skim (V1).
    /// Feature-flag + dry-run first rollout.
    /// </summary>
    public sealed class IbkrEodSkimOptions
    {
        public bool Enabled { get; init; } = false;
        public bool DryRun { get; init; } = true;
        public int StartMinutesBeforeClose { get; init; } = 60;
        public int MaxRetries { get; init; } = 3;
        public decimal MinNetProfitUsd { get; init; } = 2.00m;
        public string ReasonTag { get; init; } = "EOD-SKIM";
        public List<string> ExcludeSymbols { get; init; } = new();
    }
}
