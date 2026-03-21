using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Sažetak stanja tržišta za simbol (najčešće 1s+ update).
/// Ovo ćemo koristiti da generišemo MarketQuote za strategiju.
/// </summary>
public sealed record TickerUpdate(
    CryptoSymbol Symbol,
    DateTime TimestampUtc,
    decimal? Bid,
    decimal? Ask,
    decimal? Last,
    decimal? Volume24h,
    decimal? BidSize = null,
    decimal? AskSize = null);