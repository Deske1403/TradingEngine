using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Config;

/// <summary>
/// Podešavanja za jedan kripto par na konkretnoj berzi.
/// Ovo ćeš puniti iz appsettings (npr. appsettings.crypto.json).
/// </summary>
public sealed class CryptoSymbolSettings
{
    /// <summary>
    /// Berza kojoj simbol pripada (Kraken, Bitfinex, Deribit...).
    /// </summary>
    public CryptoExchangeId ExchangeId { get; set; }

    /// <summary>
    /// Kako berza zove simbol, npr. "XBTUSDT" ili "BTC/USDT".
    /// </summary>
    public string NativeSymbol { get; set; } = string.Empty;

    /// <summary>
    /// Base asset, npr. "BTC".
    /// </summary>
    public string BaseAsset { get; set; } = string.Empty;

    /// <summary>
    /// Quote asset, npr. "USDT".
    /// </summary>
    public string QuoteAsset { get; set; } = string.Empty;

    /// <summary>
    /// Da li je ovaj simbol trenutno aktivan u našem engine-u.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimalna količina koju berza dozvoljava po nalogu.
    /// </summary>
    public decimal MinQuantity { get; set; }

    /// <summary>
    /// Korak za količinu (step size), npr. 0.0001.
    /// </summary>
    public decimal QuantityStep { get; set; }

    /// <summary>
    /// Maksimalni deo crypto kapitala po jednom trejdu za ovaj simbol (0.0–1.0).
    /// Npr. 0.1 = max 10% ukupnog crypto capital-a u jednom trejdu.
    /// </summary>
    public decimal MaxRiskFractionPerTrade { get; set; }

    /// <summary>
    /// Maksimalni deo crypto kapitala ukupno u ovom simbolu (0.0–1.0).
    /// Npr. 0.3 = max 30% crypto capital-a u tom coinu.
    /// </summary>
    public decimal MaxExposureFraction { get; set; }

    /// <summary>
    /// Opcioni budget u USD za ovaj simbol.
    /// Ako nije postavljeno, koristi se globalni Trading:PerSymbolBudgetUsd.
    /// </summary>
    public decimal? PerSymbolBudgetUsd { get; set; }
}
