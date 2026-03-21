using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

public enum TradeSide
{
    Buy = 1,
    Sell = 2
}

/// <summary>
/// Pojedinačan trade sa berze (last trade tick).
/// Može da se koristi za volumen, agresora i price action.
/// </summary>
public sealed record TradeTick(
    CryptoSymbol Symbol,
    DateTime TimestampUtc,
    decimal Price,
    decimal Quantity,
    TradeSide Side,
    long? TradeId = null);