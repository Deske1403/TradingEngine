using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Minimalni trading API koji svaki kripto adapter mora da implementira.
/// Sve konkretne berze (Kraken/Bitfinex/Deribit) će raditi kroz ovaj interfejs.
/// </summary>
public interface ICryptoTradingApi
{
    CryptoExchangeId ExchangeId { get; }

    Task<PlaceOrderResult> PlaceLimitOrderAsync(
        CryptoSymbol symbol,
        CryptoOrderSide side,
        decimal quantity,
        decimal price,
        CancellationToken ct,
        int flags = 0,
        int? gid = null,
        decimal? priceOcoStop = null);

    Task<PlaceOrderResult> PlaceStopOrderAsync(
        CryptoSymbol symbol,
        CryptoOrderSide side,
        decimal quantity,
        decimal stopPrice,
        decimal? limitPrice,
        CancellationToken ct,
        int flags = 0,
        int? gid = null);

    Task<bool> CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken ct);

    Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(
        CancellationToken ct);

    Task<IReadOnlyList<BalanceInfo>> GetBalancesAsync(
        CancellationToken ct);
}