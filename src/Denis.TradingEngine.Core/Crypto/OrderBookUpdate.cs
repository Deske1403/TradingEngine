namespace Denis.TradingEngine.Core.Crypto;

/// <summary>
/// Snapshot ili delta order book-a za jedan simbol.
/// Implementacija po berzi odlučuje da li šalje full ili delta,
/// ali naš core za sada vidi samo celokupne levele koje nam trebaju.
/// </summary>
public sealed record OrderBookUpdate(
    CryptoSymbol Symbol,
    DateTime TimestampUtc,
    IReadOnlyList<(decimal Price, decimal Quantity)> Bids,
    IReadOnlyList<(decimal Price, decimal Quantity)> Asks);

