using System;
using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Neutralni prikaz otvorenog naloga na kripto berzi.
/// Ovo koristi risk/monitoring sloj i adapter ka Core-u.
/// </summary>
public sealed record OpenOrderInfo(
    string ExchangeOrderId,
    CryptoSymbol Symbol,
    CryptoOrderSide Side,
    CryptoOrderStatus Status,
    decimal Price,
    decimal Quantity,
    decimal FilledQuantity,
    DateTime CreatedUtc,
    DateTime? UpdatedUtc);