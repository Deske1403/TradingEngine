namespace Denis.TradingEngine.Exchange.Crypto.Config;

/// <summary>
/// Globalni risk profil za jednu kripto berzu u okviru našeg sistema.
/// Ovo je "gornji plafon" – simbol-level podešavanja su unutar ovih granica.
/// </summary>
public sealed class CryptoRiskProfile
{
    /// <summary>
    /// Maksimalni dnevni gubitak (u odnosu na crypto equity), npr. 0.02 = -2%.
    /// Kada se pređe, dan se zaključava.
    /// </summary>
    public decimal DailyLossStopFraction { get; set; }

    /// <summary>
    /// Maksimalan broj trejdova dnevno (svi simboli zajedno).
    /// </summary>
    public int MaxTradesPerDay { get; set; }

    /// <summary>
    /// Maksimalan broj trejdova dnevno po simbolu.
    /// </summary>
    public int MaxTradesPerSymbolPerDay { get; set; }

    /// <summary>
    /// Maksimalan broj otvorenih pozicija u isto vreme.
    /// </summary>
    public int MaxOpenPositions { get; set; }

    /// <summary>
    /// Minimalni notional (u quote valuati) po trejdu – ispod ovoga ne trejdujemo.
    /// Npr. 10 USDT.
    /// </summary>
    public decimal MinNotionalPerTrade { get; set; }

    /// <summary>
    /// Maksimalni deo ukupnog crypto kapitala po jednom trejdu (0.0–1.0).
    /// </summary>
    public decimal MaxNotionalFractionPerTrade { get; set; }

    /// <summary>
    /// Maksimalni deo ukupnog crypto kapitala u jednom simbolu (0.0–1.0).
    /// </summary>
    public decimal MaxNotionalFractionPerSymbol { get; set; }
}