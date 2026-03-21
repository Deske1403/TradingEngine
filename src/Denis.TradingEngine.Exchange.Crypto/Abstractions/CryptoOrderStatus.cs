namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Pojednostavljen status naloga na berzi.
/// Držimo ga neutralno, da možemo sve berze da mapiramo na isto.
/// </summary>
public enum CryptoOrderStatus
{
    Unknown = 0,
    New = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Canceled = 4,
    Rejected = 5,
    Expired = 6
}