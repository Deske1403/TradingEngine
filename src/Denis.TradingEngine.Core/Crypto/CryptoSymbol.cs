namespace Denis.TradingEngine.Core.Crypto;

/// <summary>
/// Neutralni opis kripto para koji naš engine koristi.
/// Primer:
///   BaseAsset = "BTC"
///   QuoteAsset = "USDT"
///   ExchangeId = CryptoExchangeId.Kraken
///   NativeSymbol = "XBTUSDT" (kako ga zove sama berza)
/// </summary>
public sealed record CryptoSymbol(
    CryptoExchangeId ExchangeId,
    string BaseAsset,
    string QuoteAsset,
    string NativeSymbol)
{
    // npr. BTCUSDT
    public string PublicSymbol => $"{BaseAsset}{QuoteAsset}";

    // Ticker koji koristi tvoj glavni engine
    public string Ticker => PublicSymbol;

    public override string ToString()
        => $"{ExchangeId}:{PublicSymbol} ({NativeSymbol})";
}

