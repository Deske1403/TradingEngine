#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Exchange.Crypto.Config;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex;

/// <summary>
/// Centralizovani manager za Bitfinex order management.
/// Kombinuje:
/// - BitfinexPrivateWebSocketFeed (real-time order updates)
/// - BitfinexRestOrderReconciler (periodic sync kao backup)
/// 
/// Može se koristiti i u Program.cs i u CryptoTradingRunner.
/// </summary>
public sealed class BitfinexOrderManager : IAsyncDisposable
{
    private readonly BrokerOrderRepository _orderRepo;
    private readonly BitfinexTradingApi _tradingApi;
    private readonly BitfinexPrivateWebSocketFeed _privateWs;
    private readonly BitfinexRestOrderReconciler _reconciler;
    private readonly ILogger _log;

    private Task? _wsTask;
    private Task? _reconcilerTask;
    private CancellationTokenSource? _cts;

    // Event koji se poziva kada se detektuje fill-ovani order
    // Orchestrator se može subscribe-ovati na ovaj event da primeni fill
    public event Action<OrderResult>? OrderFilled;

    /// <summary>
    /// Poziva se kada se OCO partner (npr. SL) pojavi na berzi – imamo brokerOrderId ali red u bazi još nema broker_order_id.
    /// (correlationId, brokerOrderId) da orchestrator postavi BrokerOrderId u pending store.
    /// </summary>
    public event Action<string, string>? OrderLinked;

    public BitfinexOrderManager(
        BrokerOrderRepository orderRepo,
        BitfinexTradingApi tradingApi,
        CryptoExchangeSettings exchangeSettings,
        ILogger log,
        BitfinexAuthNonceProvider? nonceProvider = null)
    {
        _orderRepo = orderRepo ?? throw new ArgumentNullException(nameof(orderRepo));
        _tradingApi = tradingApi ?? throw new ArgumentNullException(nameof(tradingApi));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // Private WebSocket feed (real-time order updates)
        var wsUrl = exchangeSettings.WebSocketUrl?.Replace("api-pub", "api")
                   ?? "wss://api.bitfinex.com/ws/2"; // Private WS endpoint

        _privateWs = new BitfinexPrivateWebSocketFeed(
            wsUrl: wsUrl,
            apiKey: exchangeSettings.ApiKey ?? string.Empty,
            apiSecret: exchangeSettings.ApiSecret ?? string.Empty,
            log: log,
            nonceProvider: nonceProvider);

        // Hook order events iz Private WS-a
        _privateWs.OrderNew += OnOrderNew;
        _privateWs.OrderUpdate += OnOrderUpdate;
        _privateWs.OrderCancel += OnOrderCancel;
        _privateWs.OrderSnapshot += OnOrderSnapshot;

        // REST reconciler (periodic sync backup)
        _reconciler = new BitfinexRestOrderReconciler(
            brokerOrders: orderRepo,
            api: tradingApi,
            log: log);

        // Hook reconciliation fill events da prosleđuju u OrderFilled event
        _reconciler.OrderFilled += result => OrderFilled?.Invoke(result);
    }

    private async void OnOrderNew(BitfinexOrderEvent evt)
    {
        try
        {
            var brokerOrderId = $"Bitfinex:{evt.OrderId}";

            // Da li ovaj order već postoji u bazi (npr. TP koji smo mi poslali)?
            var dbOrder = await _orderRepo.GetByBrokerOrderIdAsync(brokerOrderId, CancellationToken.None).ConfigureAwait(false);

            if (dbOrder != null)
            {
                // Red već ima broker_order_id – ažuriraj status
                var orderInfo = await _tradingApi.GetOrderAsync(evt.OrderId, CancellationToken.None).ConfigureAwait(false);
                if (orderInfo == null) return;
                var dbStatus = MapToDbStatusFromEnum(orderInfo.Status, orderInfo.FilledQuantity, orderInfo.Quantity);
                await _orderRepo.UpdateStatusByBrokerOrderIdAsync(
                    brokerOrderId,
                    dbStatus,
                    lastMsg: $"ws-order-new: {evt.Status}",
                    forCrypto: true,
                    CancellationToken.None).ConfigureAwait(false);
                _log.Information("[BFX-ORDER-MGR] Order NEW updated in DB: brokerOrderId={BrokerOrderId} status={Status}",
                    brokerOrderId, dbStatus);
                EmitFilledIfNeeded(orderInfo, brokerOrderId, evt);
                return;
            }

            // Nema reda sa ovim broker_order_id – verovatno OCO partner (SL) koji je Bitfinex kreirao
            var orderInfo2 = await _tradingApi.GetOrderAsync(evt.OrderId, CancellationToken.None).ConfigureAwait(false);
            if (orderInfo2 == null)
            {
                _log.Debug("[BFX-ORDER-MGR] Order NEW not found on exchange: orderId={OrderId}", evt.OrderId);
                return;
            }

            var symbol = orderInfo2.Symbol?.PublicSymbol ?? evt.NativeSymbol?.Replace("t", "", StringComparison.Ordinal).Replace("UST", "USDT", StringComparison.OrdinalIgnoreCase) ?? "";
            var side = orderInfo2.Side == CryptoOrderSide.Sell ? "sell" : "buy";
            var qty = orderInfo2.Quantity;
            // Linkuj SAMO OCO partner (stop/SL). Entry limit ordere ne linkujemo ovde – broker_order_id stiže iz Place response u orchestratoru.
            // Ako bismo linkovali i "limit", TP event može pogrešno da "zalepi" broker id na SL red.
            // Kad je tip poznat i nije STOP, preskačemo stop-link korak.
            var typeKnown = !string.IsNullOrWhiteSpace(evt.OrderType);
            var isStopType = IsStopOrderType(evt.OrderType);
            if (!typeKnown || isStopType)
            {
                var foundId = await _orderRepo.FindSubmittedWithoutBrokerIdAsync("Bitfinex", symbol, side, qty, "stop", relaxQtyMatch: true, CancellationToken.None).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(foundId))
                {
                    await _orderRepo.MarkSentAsync(foundId, brokerOrderId, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    _log.Information("[BFX-ORDER-MGR] OCO partner linked: id={CorrId} brokerOrderId={BrokerId} sym={Sym} type={Type}",
                        foundId, brokerOrderId, symbol, evt.OrderType);
                    OrderLinked?.Invoke(foundId, brokerOrderId);
                    return;
                }
            }

            // Nije OCO partner – možda stari order; ažuriraj po broker_order_id (možda neće naći)
            var dbStatus2 = MapToDbStatusFromEnum(orderInfo2.Status, orderInfo2.FilledQuantity, orderInfo2.Quantity);
            await _orderRepo.UpdateStatusByBrokerOrderIdAsync(
                brokerOrderId,
                dbStatus2,
                lastMsg: $"ws-order-new: {evt.Status}",
                forCrypto: true,
                CancellationToken.None).ConfigureAwait(false);
            EmitFilledIfNeeded(orderInfo2, brokerOrderId, evt);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BFX-ORDER-MGR] Error handling OrderNew event");
        }
    }

    private void EmitFilledIfNeeded(OpenOrderInfo orderInfo, string brokerOrderId, BitfinexOrderEvent evt)
    {
        var dbStatus = MapToDbStatusFromEnum(orderInfo.Status, orderInfo.FilledQuantity, orderInfo.Quantity);
        if (dbStatus != "filled" || orderInfo.FilledQuantity < orderInfo.Quantity)
            return;
        var orderResult = new OrderResult(
            BrokerOrderId: brokerOrderId,
            Status: "Filled",
            FilledQuantity: orderInfo.FilledQuantity,
            AverageFillPrice: orderInfo.Price,
            CommissionAndFees: null,
            Message: $"WS order new: {evt.Status}",
            TimestampUtc: DateTime.UtcNow);
        OrderFilled?.Invoke(orderResult);
        _log.Information("[BFX-ORDER-MGR] Order FILLED event emitted (from OrderNew): brokerId={BrokerId} qty={Qty} px={Px}",
            brokerOrderId, orderInfo.FilledQuantity, orderInfo.Price);
    }

    private async void OnOrderUpdate(BitfinexOrderEvent evt)
    {
        try
        {
            var brokerOrderId = $"Bitfinex:{evt.OrderId}";

            // Umesto da vučemo iz baze, koristimo direktno Bitfinex API da dobijemo punu informaciju
            var orderInfo = await _tradingApi.GetOrderAsync(evt.OrderId, CancellationToken.None).ConfigureAwait(false);

            if (orderInfo == null)
            {
                _log.Debug("[BFX-ORDER-MGR] Order UPDATE not found on exchange: orderId={OrderId}", evt.OrderId);
                return;
            }

            var dbStatus = MapToDbStatusFromEnum(orderInfo.Status, orderInfo.FilledQuantity, orderInfo.Quantity);

            // Pronađi order u bazi direktno po brokerOrderId da ažuriramo status
            var dbOrder = await _orderRepo.GetByBrokerOrderIdAsync(brokerOrderId, CancellationToken.None).ConfigureAwait(false);

            if (dbOrder != null)
            {
                await _orderRepo.UpdateStatusAsync(
                    dbOrder.Id,
                    dbStatus,
                    lastMsg: $"ws-order-update: filled={orderInfo.FilledQuantity:F8}/{orderInfo.Quantity:F8}",
                    forCrypto: true,
                    CancellationToken.None).ConfigureAwait(false);

                _log.Information("[BFX-ORDER-MGR] Order UPDATE in DB: id={Id} filled={Filled}/{Qty} status={Status}",
                    dbOrder.Id, orderInfo.FilledQuantity, orderInfo.Quantity, dbStatus);
            }

            // Ako je order fill-ovan (filled ili fully filled), emituj OrderFilled event
            if (dbStatus == "filled" && orderInfo.FilledQuantity >= orderInfo.Quantity)
            {
                var orderResult = new OrderResult(
                    BrokerOrderId: brokerOrderId,
                    Status: "Filled",
                    FilledQuantity: orderInfo.FilledQuantity,
                    AverageFillPrice: orderInfo.Price,
                    CommissionAndFees: null,
                    Message: $"WS order update: {evt.Status}",
                    TimestampUtc: DateTime.UtcNow);

                OrderFilled?.Invoke(orderResult);
                _log.Information("[BFX-ORDER-MGR] Order FILLED event emitted: brokerId={BrokerId} qty={Qty} px={Px}",
                    brokerOrderId, orderInfo.FilledQuantity, orderInfo.Price);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BFX-ORDER-MGR] Error handling OrderUpdate event");
        }
    }

    private async void OnOrderCancel(BitfinexOrderEvent evt)
    {
        try
        {
            var brokerOrderId = $"Bitfinex:{evt.OrderId}";

            // Bitfinex šalje "oc" event i za EXECUTED order-e (kada se ispune)
            // Proveri da li je order zapravo ispunjen
            var isExecuted = !string.IsNullOrWhiteSpace(evt.Status) && 
                            evt.Status.Contains("EXECUTED", StringComparison.OrdinalIgnoreCase);

            if (isExecuted)
            {
                // Order je ispunjen - tretiraj kao fill, ne kao cancel
                // Koristi podatke direktno iz WebSocket event-a jer GetOrderAsync može da vrati null za EXECUTED order-e
                var filledQty = evt.FilledQuantity > 0m ? evt.FilledQuantity : evt.Quantity;
                var fillPrice = evt.Price > 0m ? evt.Price : 0m;
                
                _log.Information("[BFX-ORDER-MGR] Order EXECUTED detected: brokerOrderId={BrokerOrderId} qty={Qty} filled={Filled} price={Price} status={Status}",
                    brokerOrderId, evt.Quantity, filledQty, fillPrice, evt.Status);

                // Ažuriraj status na "filled"
                await _orderRepo.UpdateStatusByBrokerOrderIdAsync(
                    brokerOrderId,
                    "filled",
                    lastMsg: $"ws-order-executed: filled={filledQty:F8}/{evt.Quantity:F8}",
                    forCrypto: true,
                    CancellationToken.None).ConfigureAwait(false);

                _log.Information("[BFX-ORDER-MGR] Order EXECUTED (filled) in DB: brokerOrderId={BrokerOrderId} qty={Qty} filled={Filled} price={Price}",
                    brokerOrderId, evt.Quantity, filledQty, fillPrice);

                // Emituj fill event - koristi podatke iz WebSocket event-a
                if (filledQty > 0m && fillPrice > 0m)
                {
                    var orderResult = new OrderResult(
                        BrokerOrderId: brokerOrderId,
                        Status: "Filled",
                        FilledQuantity: filledQty,
                        AverageFillPrice: fillPrice,
                        CommissionAndFees: null,
                        Message: $"WS order executed: {evt.Status}",
                        TimestampUtc: DateTime.UtcNow);

                    OrderFilled?.Invoke(orderResult);
                    _log.Information("[BFX-ORDER-MGR] Order FILLED event emitted: brokerId={BrokerId} qty={Qty} px={Px}",
                        brokerOrderId, filledQty, fillPrice);
                }
                else
                {
                    _log.Warning("[BFX-ORDER-MGR] Order EXECUTED but invalid fill data: qty={Qty} filled={Filled} price={Price}",
                        evt.Quantity, filledQty, fillPrice);
                }
            }
            else
            {
                // Stvarno canceled - ažuriraj status
                await _orderRepo.UpdateStatusByBrokerOrderIdAsync(
                    brokerOrderId,
                    "canceled",
                    lastMsg: "ws-order-cancel",
                    forCrypto: true,
                    CancellationToken.None).ConfigureAwait(false);

                // Loguj detalje o cancel-u
                var statusInfo = !string.IsNullOrWhiteSpace(evt.Status) 
                    ? $" status={evt.Status}" 
                    : "";
                _log.Information("[BFX-ORDER-MGR] Order CANCELED in DB: brokerOrderId={BrokerOrderId}{Status} qty={Qty} price={Price}",
                    brokerOrderId, statusInfo, evt.Quantity, evt.Price);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BFX-ORDER-MGR] Error handling OrderCancel event");
        }
    }

    private async void OnOrderSnapshot(BitfinexOrderEvent[] orders)
    {
        try
        {
            _log.Information("[BFX-ORDER-MGR] Order snapshot received: {Count} orders", orders.Length);
            // Snapshot se koristi za initial sync, reconciliation će se pobrinuti za status update-e
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BFX-ORDER-MGR] Error handling OrderSnapshot event");
        }
    }

    private static string MapToDbStatusFromEnum(CryptoOrderStatus status, decimal filledQty, decimal qty)
    {
        // DB status strings: filled, canceled, rejected, partially_filled, sent, submitted...
        return status switch
        {
            CryptoOrderStatus.Filled => "filled",
            CryptoOrderStatus.Canceled => "canceled",
            CryptoOrderStatus.Rejected => "rejected",
            CryptoOrderStatus.PartiallyFilled => "partially_filled",
            CryptoOrderStatus.New => "sent", // "active" nije u constraint-u, koristimo "sent" za aktivne order-e
            CryptoOrderStatus.Unknown => InferFromFill(filledQty, qty),
            _ => InferFromFill(filledQty, qty)
        };
    }

    private static bool IsStopOrderType(string? orderType)
    {
        return !string.IsNullOrWhiteSpace(orderType) &&
               orderType.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string MapToDbStatus(string bitfinexStatus, decimal filledQty, decimal qty)
    {
        // Mapiraj Bitfinex status string u DB status
        if (string.IsNullOrWhiteSpace(bitfinexStatus))
            return InferFromFill(filledQty, qty);

        var status = bitfinexStatus.ToUpperInvariant();

        if (status.Contains("EXECUTED", StringComparison.OrdinalIgnoreCase))
            return "filled";

        if (status.Contains("CANCEL", StringComparison.OrdinalIgnoreCase))
            return "canceled";

        if (status.Contains("REJECT", StringComparison.OrdinalIgnoreCase))
            return "rejected";

        if (status.Contains("PART", StringComparison.OrdinalIgnoreCase))
            return "partially_filled";

        if (status.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase))
            return "sent"; // "active" nije u constraint-u, koristimo "sent" za aktivne order-e

        return InferFromFill(filledQty, qty);
    }

    private static string InferFromFill(decimal filledQty, decimal qty)
    {
        if (qty <= 0m)
            return "unknown";

        if (filledQty >= qty && qty > 0m)
            return "filled";

        if (filledQty > 0m && filledQty < qty)
            return "partially_filled";

        return "sent"; // "active" nije u constraint-u, koristimo "sent" za aktivne order-e
    }

    /// <summary>
    /// Pokreće order management (Private WS + periodic reconciliation).
    /// </summary>
    public void Start(CancellationToken ct)
    {
        if (_wsTask != null || _reconcilerTask != null)
        {
            _log.Warning("[BFX-ORDER-MGR] Already started");
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _cts.Token;

        // Start Private WebSocket feed (real-time order updates)
        _wsTask = Task.Run(async () =>
        {
            var reconnectDelay = TimeSpan.FromSeconds(5);

            while (!linkedCt.IsCancellationRequested)
            {
                try
                {
                    // Hook order events iz Private WS-a
                    await _privateWs.RunAsync(linkedCt).ConfigureAwait(false);

                    if (linkedCt.IsCancellationRequested)
                        break;

                    _log.Warning("[BFX-ORDER-MGR] Private WS loop ended unexpectedly. Reconnecting in {DelaySec}s", reconnectDelay.TotalSeconds);
                    await Task.Delay(reconnectDelay, linkedCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "[BFX-ORDER-MGR] Private WS error. Reconnecting in {DelaySec}s", reconnectDelay.TotalSeconds);
                    try
                    {
                        await Task.Delay(reconnectDelay, linkedCt).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _log.Information("[BFX-ORDER-MGR] Private WS stopped");
        }, linkedCt);

        // Start periodic reconciliation (backup sync svakih 60 sekundi)
        _reconcilerTask = Task.Run(async () =>
        {
            var interval = TimeSpan.FromSeconds(60);

            while (!linkedCt.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, linkedCt).ConfigureAwait(false);
                    await _reconciler.ReconcileOnceAsync(linkedCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "[BFX-ORDER-MGR] Reconciliation error");
                    // Continue sa retry-om posle intervala
                }
            }
        }, linkedCt);

        _log.Information("[BFX-ORDER-MGR] Started (Private WS + Reconciliation every 60s)");
    }

    /// <summary>
    /// Zaustavlja order management.
    /// </summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();

        if (_wsTask != null)
        {
            try
            {
                await _wsTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[BFX-ORDER-MGR] Error stopping WS task");
            }
        }

        if (_reconcilerTask != null)
        {
            try
            {
                await _reconcilerTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[BFX-ORDER-MGR] Error stopping reconciler task");
            }
        }

        await _privateWs.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();

        _log.Information("[BFX-ORDER-MGR] Stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
