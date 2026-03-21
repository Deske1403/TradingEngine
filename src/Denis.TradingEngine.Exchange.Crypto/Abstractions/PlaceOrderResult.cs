namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Rezultat pokušaja slanja naloga na kripto berzu.
/// </summary>
public sealed record PlaceOrderResult(
    bool Accepted,
    string? ExchangeOrderId,
    string? RejectReason);