#nullable enable

namespace Denis.TradingEngine.Core.Risk
{
    /// <summary>
    /// Granice rizika koje sistem mora uvek da poštuje.
    /// Sve vrednosti su procentualni delovi ukupnog kapitala.
    /// </summary>
    public sealed record RiskLimits(
        decimal MaxRiskPerTradeFraction,      // npr. 0.01m (1% po trgovini)  – trenutno ga ne koristiš
        decimal MaxExposurePerSymbolFrac,     // npr. 0.03m (3% po simbolu)
        decimal DailyLossStopFraction,        // npr. 0.01m (pauza ako izgubimo 1% u danu)
        decimal MaxPerTradeFrac = 0.3m,       // npr. 0.3m = max 30% FREE cash po jednom ulazu
        decimal MinAtrFraction = 0.002m      // minimalni ATR kao % cene (npr. 0.2% = 0.002m) - floor za ATR sizing
    );
}