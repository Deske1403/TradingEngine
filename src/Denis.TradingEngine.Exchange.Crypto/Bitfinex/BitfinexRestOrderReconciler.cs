#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex;

public sealed class BitfinexRestOrderReconciler
{
    // 2026-02-20: restart/reconcile fix for Bitfinex.
    // Cause: trade-hist can report tiny qty delta (e.g. 0.00699999 vs 0.007),
    // which previously downgraded a filled order to partially_filled after restart.
    // Scope: crypto-only reconciliation path.
    private const decimal FillQtyEpsilon = 0.0000001m;

    private readonly BrokerOrderRepository _brokerOrders;
    private readonly BitfinexTradingApi _api;
    private readonly ILogger _log;

    // Event koji se poziva kada reconciliation detektuje fill-ovani order
    public event Action<OrderResult>? OrderFilled;

    public BitfinexRestOrderReconciler(
        BrokerOrderRepository brokerOrders,
        BitfinexTradingApi api,
        ILogger log)
    {
        _brokerOrders = brokerOrders ?? throw new ArgumentNullException(nameof(brokerOrders));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _log = log ?? Log.ForContext<BitfinexRestOrderReconciler>();
    }

    public async Task ReconcileOnceAsync(CancellationToken ct)
    {
        // 1) Uzmi sve nedavne ordere iz baze (ne samo "open" - uključuje i "sent" koji su možda fill-ovani)
        var dbOpen = await _brokerOrders.GetRecentOrdersWithStatusForExchangeAsync("Bitfinex", hoursBack: 24, ct).ConfigureAwait(false);
        if (dbOpen.Count == 0)
            return;

        // 2) Uzmi SVE order-e sa berze (ne samo open) - koristimo GetOpenOrdersAsync koji vraća sve aktivne
        // Bitfinex API vraća sve order-e u GetOpenOrdersAsync, uključujući i one koji su fill-ovani ali još nisu zatvoreni
        IReadOnlyList<OpenOrderInfo> exOrders;
        try
        {
            exOrders = await _api.GetOpenOrdersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BFX-RECON] GetOpenOrdersAsync failed");
            return;
        }

        var ordersById = new Dictionary<string, OpenOrderInfo>(StringComparer.Ordinal);
        foreach (var o in exOrders)
        {
            if (!string.IsNullOrWhiteSpace(o.ExchangeOrderId))
                ordersById[o.ExchangeOrderId] = o;
        }

        foreach (var row in dbOpen)
        {
            if (ct.IsCancellationRequested)
                break;

            if (!IsBitfinex(row.Exchange))
                continue;

            var exchangeOrderId = ExtractExchangeOrderId(row.BrokerOrderId);
            if (string.IsNullOrWhiteSpace(exchangeOrderId))
                continue;

            // Ako je order i dalje na berzi (u listi), ažuriraj status direktno
            if (ordersById.TryGetValue(exchangeOrderId, out var orderInfo))
            {
                var mapped = MapToDbStatus(orderInfo.Status, orderInfo.FilledQuantity, orderInfo.Quantity);
                decimal emitQty = orderInfo.FilledQuantity;
                decimal emitPx = orderInfo.Price;
                string lastMsg = "rest-sync active";

                // Safety fallback: ako open-order endpoint vrati ACTIVE, proveri trades/history.
                // Dešava se da WS event izostane, a trade je već evidentiran.
                if (mapped == "sent")
                {
                    BitfinexTradingApi.OrderTradeSnapshot? trade = null;
                    try
                    {
                        trade = await _api.GetLatestTradeForOrderAsync(exchangeOrderId, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[BFX-RECON] trades/hist fallback failed id={Id}", exchangeOrderId);
                    }

                    if (trade is not null && trade.FilledQuantity > 0m)
                    {
                        emitQty = trade.FilledQuantity;
                        emitPx = trade.Price;
                        var epsilonPromotedToFilled =
                            trade.FilledQuantity < row.Qty &&
                            trade.FilledQuantity >= row.Qty - FillQtyEpsilon;

                        if (epsilonPromotedToFilled)
                        {
                            _log.Information(
                                "[BFX-RECON] EPSILON-FILL promoted to filled id={Id} filled={Filled} qty={Qty} eps={Eps}",
                                exchangeOrderId, trade.FilledQuantity, row.Qty, FillQtyEpsilon);
                        }

                        if (trade.FilledQuantity >= row.Qty - FillQtyEpsilon)
                            mapped = "filled";
                        else
                            mapped = "partially_filled";

                        lastMsg = "rest-sync trade-hist";
                    }
                }

                var statusChanged = !string.Equals(row.Status, mapped, StringComparison.OrdinalIgnoreCase);

                if (statusChanged)
                {
                    await _brokerOrders.UpdateStatusAsyncCryptoGuardFilledAsync(row.Id, mapped, lastMsg: lastMsg, ct).ConfigureAwait(false);
                }

                // Ako je order fill-ovan, emituj OrderFilled event
                if (mapped == "filled" && emitQty > 0m && statusChanged)
                {
                    var brokerOrderId = row.BrokerOrderId ?? $"Bitfinex:{exchangeOrderId}";
                    var orderResult = new OrderResult(
                        BrokerOrderId: brokerOrderId,
                        Status: "Filled",
                        FilledQuantity: emitQty,
                        AverageFillPrice: emitPx,
                        CommissionAndFees: null,
                        Message: "REST reconciliation: active->filled",
                        TimestampUtc: DateTime.UtcNow);

                    OrderFilled?.Invoke(orderResult);
                    _log.Information("[BFX-RECON] Order FILLED event emitted: brokerId={BrokerId} qty={Qty} px={Px}",
                        brokerOrderId, emitQty, emitPx);
                }
                continue;
            }

            // Order nije u listi aktivnih - proveri final status direktno sa berze
            OpenOrderInfo? final;
            try
            {
                final = await _api.GetOrderAsync(exchangeOrderId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[BFX-RECON] GetOrderAsync failed id={Id}", exchangeOrderId);
                continue;
            }

            if (final is null)
            {
                // Fallback kada /orders ne vraća final status: proveri trades history po order ID.
                BitfinexTradingApi.OrderTradeSnapshot? trade = null;
                try
                {
                    trade = await _api.GetLatestTradeForOrderAsync(exchangeOrderId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[BFX-RECON] trades/hist fallback failed id={Id}", exchangeOrderId);
                }

                if (trade is null || trade.FilledQuantity <= 0m)
                    continue;

                var epsilonPromotedToFilled =
                    trade.FilledQuantity < row.Qty &&
                    trade.FilledQuantity >= row.Qty - FillQtyEpsilon;

                if (epsilonPromotedToFilled)
                {
                    _log.Information(
                        "[BFX-RECON] EPSILON-FILL promoted to filled (trade-hist) id={Id} filled={Filled} qty={Qty} eps={Eps}",
                        exchangeOrderId, trade.FilledQuantity, row.Qty, FillQtyEpsilon);
                }

                var inferred = trade.FilledQuantity >= row.Qty - FillQtyEpsilon ? "filled" : "partially_filled";
                var inferredChanged = !string.Equals(row.Status, inferred, StringComparison.OrdinalIgnoreCase);
                if (inferredChanged)
                {
                    await _brokerOrders.UpdateStatusAsyncCryptoGuardFilledAsync(row.Id, inferred, lastMsg: $"rest-sync trade-hist {inferred}", ct).ConfigureAwait(false);
                }

                if (inferred == "filled" && inferredChanged)
                {
                    var brokerOrderId = row.BrokerOrderId ?? $"Bitfinex:{exchangeOrderId}";
                    var orderResult = new OrderResult(
                        BrokerOrderId: brokerOrderId,
                        Status: "Filled",
                        FilledQuantity: trade.FilledQuantity,
                        AverageFillPrice: trade.Price,
                        CommissionAndFees: null,
                        Message: "REST reconciliation: trade-hist",
                        TimestampUtc: trade.TimestampUtc);

                    OrderFilled?.Invoke(orderResult);
                    _log.Information("[BFX-RECON] Order FILLED event emitted (trade-hist): brokerId={BrokerId} qty={Qty} px={Px}",
                        brokerOrderId, trade.FilledQuantity, trade.Price);
                }
                continue;
            }

            var finalMapped = MapToDbStatus(final.Status, final.FilledQuantity, final.Quantity);
            var finalChanged = !string.Equals(row.Status, finalMapped, StringComparison.OrdinalIgnoreCase);

            if (!string.Equals(finalMapped, "unknown", StringComparison.Ordinal))
            {
                if (finalChanged)
                {
                    await _brokerOrders.UpdateStatusAsyncCryptoGuardFilledAsync(row.Id, finalMapped, lastMsg: $"rest-sync {finalMapped}", ct).ConfigureAwait(false);
                }

                // Ako je order fill-ovan, emituj OrderFilled event
                if (finalMapped == "filled" && final.FilledQuantity >= final.Quantity - FillQtyEpsilon && finalChanged)
                {
                    var brokerOrderId = row.BrokerOrderId ?? $"Bitfinex:{exchangeOrderId}";
                    var orderResult = new OrderResult(
                        BrokerOrderId: brokerOrderId,
                        Status: "Filled",
                        FilledQuantity: final.FilledQuantity,
                        AverageFillPrice: final.Price,
                        CommissionAndFees: null,
                        Message: $"REST reconciliation: {final.Status}",
                        TimestampUtc: DateTime.UtcNow);

                    OrderFilled?.Invoke(orderResult);
                    _log.Information("[BFX-RECON] Order FILLED event emitted: brokerId={BrokerId} qty={Qty} px={Px}",
                        brokerOrderId, final.FilledQuantity, final.Price);
                }
            }
        }
    }

    private static bool IsBitfinex(string? exchange)
        => exchange != null && exchange.Equals("Bitfinex", StringComparison.OrdinalIgnoreCase);

    private static string ExtractExchangeOrderId(string? brokerOrderId)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
            return string.Empty;

        // očekujemo format: "Bitfinex:228366175014"
        var idx = brokerOrderId.IndexOf(':');
        if (idx <= 0 || idx >= brokerOrderId.Length - 1)
            return string.Empty;

        return brokerOrderId[(idx + 1)..].Trim();
    }

    private static string MapToDbStatus(CryptoOrderStatus st, decimal filledQty, decimal qty)
    {
        // DB status strings: filled, canceled, rejected, partially_filled, sent, submitted...
        return st switch
        {
            CryptoOrderStatus.Filled => "filled",
            CryptoOrderStatus.Canceled => "canceled",
            CryptoOrderStatus.Rejected => "rejected",
            CryptoOrderStatus.PartiallyFilled => "partially_filled",
            CryptoOrderStatus.New => "sent", // "active" nije u constraint-u, koristimo "sent" za aktivne order-e
            CryptoOrderStatus.Unknown => InferFromFill(filledQty, qty),
            _ => "unknown"
        };
    }

    private static string InferFromFill(decimal filledQty, decimal qty)
    {
        if (qty <= 0m)
            return "unknown";

        if (filledQty >= qty - FillQtyEpsilon && qty > 0m)
            return "filled";

        if (filledQty > 0m && filledQty < qty - FillQtyEpsilon)
            return "partially_filled";

        return "unknown";
    }
}
