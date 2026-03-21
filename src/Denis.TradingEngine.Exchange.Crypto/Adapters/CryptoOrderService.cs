#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Adapters;

/// <summary>
/// Adapter koji standardni OrderRequest iz core-a mapira na kripto nalog
/// preko ICryptoTradingApi.
/// </summary>
public sealed class CryptoOrderService : IOrderService
{
    private readonly ILogger _log;
    private readonly IReadOnlyDictionary<CryptoExchangeId, ICryptoTradingApi> _apis;
    private readonly ICryptoSymbolMetadataProvider _symbolMetadataProvider;

    public event Action<OrderResult>? OrderUpdated;

    public CryptoOrderService(
        IReadOnlyDictionary<CryptoExchangeId, ICryptoTradingApi> apis,
        ICryptoSymbolMetadataProvider symbolMetadataProvider,
        ILogger log)
    {
        _apis = apis ?? throw new ArgumentNullException(nameof(apis));
        _symbolMetadataProvider = symbolMetadataProvider ?? throw new ArgumentNullException(nameof(symbolMetadataProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<string> PlaceAsync(OrderRequest request)
{
    if (request is null) throw new ArgumentNullException(nameof(request));

    if (!TryResolveExchange(request.Symbol.Exchange, out var exchangeId))
    {
        throw new InvalidOperationException(
            $"CryptoOrderService: nepoznat exchange string '{request.Symbol.Exchange}'.");
    }

    if (!_apis.TryGetValue(exchangeId, out var api))
    {
        throw new InvalidOperationException(
            $"CryptoOrderService: nema ICryptoTradingApi za exchange '{exchangeId}'.");
    }

    if (!_symbolMetadataProvider.TryGetSymbol(exchangeId, request.Symbol.Ticker, out var cryptoSymbol))
    {
        throw new InvalidOperationException(
            $"CryptoOrderService: nije pronađen CryptoSymbol za {exchangeId}:{request.Symbol.Ticker}.");
    }

    if (!_symbolMetadataProvider.TryGetMetadata(cryptoSymbol, out var meta))
    {
        throw new InvalidOperationException(
            $"CryptoOrderService: nedostaju metapodaci za {exchangeId}:{cryptoSymbol.PublicSymbol}.");
    }

    var side = request.Side == OrderSide.Buy
        ? CryptoOrderSide.Buy
        : CryptoOrderSide.Sell;

    var rawQty = AdjustQuantity(request.Quantity, meta.MinQuantity, meta.QuantityStep);

    // Finalna provera - ako je i dalje ispod minimuma, odbaci order
    if (rawQty < meta.MinQuantity)
    {
        _log.Warning(
            "[CRYPTO-ORD] Order rejected: quantity {Qty} is below minimum {MinQty} for {Symbol}. Original qty was {OrigQty}",
            rawQty, meta.MinQuantity, cryptoSymbol.PublicSymbol, request.Quantity);

        throw new InvalidOperationException(
            $"CryptoOrderService: količina {rawQty} je ispod minimalne {meta.MinQuantity} za {cryptoSymbol.PublicSymbol}. " +
            $"Originalna količina: {request.Quantity}");
    }

    PlaceOrderResult result;

    switch (request.Type)
    {
        case OrderType.Limit:
        {
            if (!request.LimitPrice.HasValue)
                throw new InvalidOperationException("LimitPrice mora biti postavljen za limit nalog.");

            var limitPrice = request.LimitPrice.Value;

            var (flags, gid) = GetOcoFlagsAndGid(request.OcoGroupId);
            _log.Information(
                "[CRYPTO-ORD] PLACE-LIMIT {Exchange} {Side} {Symbol} x{Qty} @ {Px} oco={Oco}",
                exchangeId,
                side,
                cryptoSymbol.PublicSymbol,
                rawQty,
                limitPrice,
                gid.HasValue ? gid.Value.ToString() : "no");

            var priceOcoStop = request.OcoStopPrice;
            result = await api.PlaceLimitOrderAsync(
                    cryptoSymbol,
                    side,
                    rawQty,
                    limitPrice,
                    CancellationToken.None,
                    flags,
                    gid,
                    priceOcoStop)
                .ConfigureAwait(false);

            break;
        }

        case OrderType.Stop:
        {
            if (!request.StopPrice.HasValue)
                throw new InvalidOperationException("StopPrice mora biti postavljen za stop nalog.");

            var stopPrice = request.StopPrice.Value;
            // Ako želiš STOP-LIMIT, ovde bi došla LimitPrice; za STOP-MARKET ostavi null
            var stopLimitPrice = request.LimitPrice;
            
            // Za log: prikaži stvarnu vrednost koja će biti poslata (exchange može fallback na stopPrice)
            var effectiveLimitPrice = stopLimitPrice ?? stopPrice;

            var (stopFlags, stopGid) = GetOcoFlagsAndGid(request.OcoGroupId);
            _log.Information(
                "[CRYPTO-ORD] PLACE-STOP {Exchange} {Side} {Symbol} x{Qty} stop={Stop} limit={Limit} (effective={Effective}) oco={Oco}",
                exchangeId,
                side,
                cryptoSymbol.PublicSymbol,
                rawQty,
                stopPrice,
                stopLimitPrice,
                effectiveLimitPrice,
                stopGid.HasValue ? stopGid.Value.ToString() : "no");

            result = await api.PlaceStopOrderAsync(
                    cryptoSymbol,
                    side,
                    rawQty,
                    stopPrice,
                    stopLimitPrice,
                    CancellationToken.None,
                    stopFlags,
                    stopGid)
                .ConfigureAwait(false);

            break;
        }

        default:
            throw new NotSupportedException(
                $"CryptoOrderService: order type '{request.Type}' trenutno nije podržan za crypto.");
    }

    if (!result.Accepted || string.IsNullOrWhiteSpace(result.ExchangeOrderId))
    {
        var reason = result.RejectReason ?? "unknown";
        _log.Warning(
            "[CRYPTO-ORD] REJECTED {Exchange} {Symbol}: {Reason}",
            exchangeId,
            cryptoSymbol.PublicSymbol,
            reason);

        throw new InvalidOperationException($"Kripto nalog odbijen: {reason}");
    }

    // Interni brokerOrderId format: "BITFINEX:123456789"
    var brokerOrderId = $"{exchangeId}:{result.ExchangeOrderId}";
    return brokerOrderId;
}


    public async Task CancelAsync(string brokerOrderId)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            throw new ArgumentNullException(nameof(brokerOrderId));
        }

        if (!TryParseBrokerOrderId(brokerOrderId, out var exchangeId, out var nativeOrderId))
        {
            throw new InvalidOperationException($"CryptoOrderService: nevalidan brokerOrderId format '{brokerOrderId}'.");
        }

        if (!_apis.TryGetValue(exchangeId, out var api))
        {
            throw new InvalidOperationException($"CryptoOrderService: nema ICryptoTradingApi za exchange '{exchangeId}'.");
        }

        _log.Information("[CRYPTO-ORD] CANCEL {Exchange} orderId={OrderId}", exchangeId, nativeOrderId);

        var ok = await api.CancelOrderAsync(nativeOrderId, CancellationToken.None)
            .ConfigureAwait(false);

        if (!ok)
        {
            _log.Warning("[CRYPTO-ORD] Cancel nije potvrđen od strane berze: {Exchange} orderId={OrderId}",
                exchangeId,
                nativeOrderId);
        }

        // U ovoj prvoj verziji ne emituje se OrderUpdated na cancel.
        // To možemo dodati kada budemo imali bolju integraciju sa fill/exec event-ima.
    }

    /// <summary>Bitfinex OCO: flags=16384, gid=numeric from ocoGroupId. Ostale berze ignorišu.</summary>
    private static (int flags, int? gid) GetOcoFlagsAndGid(string? ocoGroupId)
    {
        if (string.IsNullOrWhiteSpace(ocoGroupId))
            return (0, null);
        var gid = Math.Abs(ocoGroupId.GetHashCode());
        if (gid == 0) gid = 1;
        return (16384, gid);
    }

    private static bool TryResolveExchange(string exchangeString, out CryptoExchangeId exchangeId)
    {
        exchangeId = CryptoExchangeId.Unknown;

        if (string.IsNullOrWhiteSpace(exchangeString))
        {
            return false;
        }

        return Enum.TryParse(exchangeString, ignoreCase: true, out exchangeId);
    }

    private static bool TryParseBrokerOrderId(string brokerOrderId, out CryptoExchangeId exchangeId, out string nativeOrderId)
    {
        exchangeId = CryptoExchangeId.Unknown;
        nativeOrderId = string.Empty;

        var sepIndex = brokerOrderId.IndexOf(':');
        if (sepIndex <= 0 || sepIndex >= brokerOrderId.Length - 1)
        {
            return false;
        }

        var exchangePart = brokerOrderId.Substring(0, sepIndex);
        nativeOrderId = brokerOrderId.Substring(sepIndex + 1);

        if (!Enum.TryParse(exchangePart, ignoreCase: true, out exchangeId))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(nativeOrderId);
    }

    private static decimal AdjustQuantity(decimal requestedQty, decimal minQty, decimal step)
    {
        if (requestedQty <= 0m)
        {
            return 0m;
        }

        if (step <= 0m)
        {
            return requestedQty;
        }

        // Koristi Round umesto Truncate da ne smanjimo previše
        // Round na najbliži step, ali ne smanjujemo ispod minQty
        var steps = Math.Round(requestedQty / step, MidpointRounding.AwayFromZero);
        var adjusted = steps * step;

        // Ako je ispod minimuma, zaokruži na minimum (ako je step dozvoljava)
        if (adjusted < minQty)
        {
            // Pokušaj da zaokružiš na minimum koristeći step
            var minSteps = Math.Ceiling(minQty / step);
            adjusted = minSteps * step;
        }

        return adjusted;
    }
}