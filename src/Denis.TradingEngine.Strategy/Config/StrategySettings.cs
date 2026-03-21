#nullable enable
using System;

namespace Denis.TradingEngine.Strategy.Config
{
    /// <summary>
    /// Podesiva pravila strategije (v1.0).
    /// </summary>
    public sealed class StrategySettings
    {
        public int ShortWindow { get; init; } = 20;          // broj uzoraka za kratki prosek
        public int LongWindow { get; init; } = 60;          // broj uzoraka za dugi prosek
        public decimal PullbackFraction { get; init; } = 0.002m;   // 0.2% pad ispod kratkog proseka
        public decimal LimitDiscountFraction { get; init; } = 0.001m; // dodatnih 0.1% za limit ulaz
        public TimeSpan MinSignalInterval { get; init; } = TimeSpan.FromMinutes(2); // razmak između signala po simbolu
        public decimal MaxSpreadFraction { get; init; } = 0.0015m;  // max dozvoljen spread (npr. 0.15% od mid-a)
        public decimal MinLongSlope { get; init; } = 0.0m;          // minimalni pozitivan nagib dugog trenda (0 => samo > 0)
    }
}