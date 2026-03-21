#nullable enable
using Denis.TradingEngine.Exchange.Crypto.Abstractions;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex;

/// <summary>
/// Order event iz Bitfinex Private WebSocket feed-a.
/// </summary>
public sealed class BitfinexOrderEvent
{
    public string OrderId { get; }
    public string NativeSymbol { get; }
    public string OrderType { get; }
    public CryptoOrderSide Side { get; }
    public decimal Quantity { get; }
    public decimal FilledQuantity { get; }
    public decimal Price { get; }
    public string Status { get; }

    public BitfinexOrderEvent(
        string orderId,
        string nativeSymbol,
        string orderType,
        CryptoOrderSide side,
        decimal quantity,
        decimal filledQuantity,
        decimal price,
        string status)
    {
        OrderId = orderId ?? throw new System.ArgumentNullException(nameof(orderId));
        NativeSymbol = nativeSymbol ?? throw new System.ArgumentNullException(nameof(nativeSymbol));
        OrderType = orderType ?? string.Empty;
        Side = side;
        Quantity = quantity;
        FilledQuantity = filledQuantity;
        Price = price;
        Status = status ?? string.Empty;
    }
}
