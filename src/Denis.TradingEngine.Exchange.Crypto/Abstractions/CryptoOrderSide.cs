namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Smer naloga na kripto berzi.
/// (Core će kasnije mapirati svoj OrderSide ↔ CryptoOrderSide.)
/// </summary>
public enum CryptoOrderSide
{
    Buy = 1,
    Sell = 2
}