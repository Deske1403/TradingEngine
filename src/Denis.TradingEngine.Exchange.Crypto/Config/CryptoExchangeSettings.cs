using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Config;
using Denis.TradingEngine.Strategy.GreenGrind;

namespace Denis.TradingEngine.Exchange.Crypto.Config;

/// <summary>
/// Konfiguracija za jednu kripto berzu (API nalozi + lista simbola + risk profil).
/// Ovo se mapa na jedan entry u appsettings (npr. "Exchanges":[...]).
/// </summary>
public sealed class CryptoExchangeSettings
{
    public CryptoExchangeId ExchangeId { get; set; }

    /// <summary>
    /// Logičko ime koje koristimo u logovima (npr. "KRAKEN_SPOT").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// REST endpoint (npr. https://api.kraken.com).
    /// </summary>
    public string RestBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// WebSocket endpoint (npr. wss://ws.kraken.com).
    /// </summary>
    public string WebSocketUrl { get; set; } = string.Empty;

    /// <summary>
    /// Da li koristimo testnet/sandbox varijantu ako berza nudi (npr. Deribit testnet).
    /// </summary>
    public bool UseTestnet { get; set; }

    /// <summary>
    /// API key / secret – zaštićeno u prod environment-u.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// Da li je ova berza aktivna (omogućena) u trenutnom run-u.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Globalni risk profil za ovu berzu.
    /// </summary>
    public CryptoRiskProfile RiskProfile { get; set; } = new();

    /// <summary>
    /// Lista simbola koje trejdujemo na ovoj berzi.
    /// </summary>
    public List<CryptoSymbolSettings> Symbols { get; set; } = new();
    
    /// <summary>
    /// Fee konfiguracija za ovu berzu.
    /// Ako nije postavljena, koriste se default vrednosti.
    /// </summary>
    public CryptoFeeSchedule? FeeSchedule { get; set; }

    /// <summary>
    /// Parametri za exit/trejding po berzi (TP/SL, trailing, max hold).
    /// Ako nije postavljeno, koriste se default vrednosti za tu berzu (npr. Bitfinex = agresivniji).
    /// </summary>
    public CryptoExchangeTradingParams? TradingParams { get; set; }

    /// <summary>
    /// Crypto-only regime gate za "green grind" (per-symbol state machine).
    /// Ako nije postavljeno, gate je isključen.
    /// </summary>
    public GreenGrindSettings? GreenGrind { get; set; }

    /// <summary>
    /// Bitfinex funding/lending feature block.
    /// Disabled by default and isolated from the spot flow.
    /// </summary>
    public BitfinexFundingOptions? Funding { get; set; }
}
