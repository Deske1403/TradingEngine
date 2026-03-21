#nullable enable
using System;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex;

/// <summary>
/// Bitfinex private WebSocket feed za order updates.
/// Emituje order events: OrderNew, OrderUpdate, OrderCancel, OrderSnapshot.
/// </summary>
public sealed class BitfinexPrivateWebSocketFeed : IAsyncDisposable
{
    private readonly Uri _wsUri;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly ILogger _log;
    private readonly BitfinexAuthNonceProvider _nonceProvider;

    private ClientWebSocket? _ws;

    // Order events
    public event Action<BitfinexOrderEvent>? OrderNew;
    public event Action<BitfinexOrderEvent>? OrderUpdate;
    public event Action<BitfinexOrderEvent>? OrderCancel;
    public event Action<BitfinexOrderEvent[]>? OrderSnapshot;

    public BitfinexPrivateWebSocketFeed(
        string wsUrl,
        string apiKey,
        string apiSecret,
        ILogger log,
        BitfinexAuthNonceProvider? nonceProvider = null)
    {
        _wsUri = new Uri(wsUrl);
        _apiKey = apiKey ?? "";
        _apiSecret = apiSecret ?? "";
        _log = log ?? Log.ForContext<BitfinexPrivateWebSocketFeed>();
        _nonceProvider = nonceProvider ?? new BitfinexAuthNonceProvider();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            _log.Warning("[BFX-PWS] Missing api key/secret. Private WS disabled");
            return;
        }

        if (_ws is not null)
        {
            try { _ws.Dispose(); } catch { }
            _ws = null;
        }

        var ws = new ClientWebSocket();
        _ws = ws;

        try
        {
            await ws.ConnectAsync(_wsUri, ct).ConfigureAwait(false);

            _log.Information("[BFX-PWS] Connected {Url}", _wsUri);

            await SendAuthAsync(ct).ConfigureAwait(false);

            var buf = new byte[1024 * 64];

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var msg = await ReceiveTextAsync(ws, buf, ct).ConfigureAwait(false);
                if (msg == null)
                    break;

                HandleMessage(msg);
            }

            _log.Information("[BFX-PWS] Loop ended. State={State}", ws.State);
        }
        finally
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "loop-end", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            try { ws.Dispose(); } catch { }
            if (ReferenceEquals(_ws, ws))
                _ws = null;
        }
    }

    private async Task SendAuthAsync(CancellationToken ct)
    {
        // Bitfinex WS auth:
        // event=auth, apiKey, authNonce, authPayload, authSig
        // payload format: "AUTH" + nonce
        var nonce = _nonceProvider.NextNonceMicros();
        var payload = "AUTH" + nonce;
        var sig = HmacSha384Hex(_apiSecret, payload);

        var obj = new
        {
            @event = "auth",
            apiKey = _apiKey,
            authNonce = nonce,
            authPayload = payload,
            authSig = sig
        };

        var json = JsonSerializer.Serialize(obj);
        await SendTextAsync(_ws!, json, ct).ConfigureAwait(false);

        _log.Information("[BFX-PWS] Sent auth");
    }

    private void HandleMessage(string json)
    {
        // Logs only. Kasnije ćemo mapirati order events i upisivati u DB.
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Event messages are objects: {"event":"auth",...}
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("event", out var ev))
                {
                    var evStr = ev.GetString();
                    if (string.Equals(evStr, "auth", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Information("[BFX-PWS] auth event: {Json}", json);
                        return;
                    }

                    if (string.Equals(evStr, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Error("[BFX-PWS] error event: {Json}", json);
                        return;
                    }

                    _log.Information("[BFX-PWS] event: {Json}", json);
                    return;
                }

                _log.Information("[BFX-PWS] obj: {Json}", json);
                return;
            }

            // Data messages are arrays: [CHAN_ID, EVENT_TYPE, ...]
            // Private channels: chanId=0, event types: "on" (new), "ou" (update), "oc" (cancel), "os" (snapshot)
            if (root.ValueKind == JsonValueKind.Array)
            {
                HandleOrderArray(root);
                return;
            }

            _log.Debug("[BFX-PWS] msg: {Json}", json);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BFX-PWS] parse failed raw={Raw}", Trunc(json, 1200));
        }
    }

    private static string HmacSha384Hex(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var msg = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA384(key);
        var hash = hmac.ComputeHash(msg);

        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var ms = new ArraySegment<byte>(buffer);
        using var acc = new System.IO.MemoryStream();

        while (true)
        {
            var res = await ws.ReceiveAsync(ms, ct).ConfigureAwait(false);
            if (res.MessageType == WebSocketMessageType.Close)
                return null;

            acc.Write(buffer, 0, res.Count);

            if (res.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(acc.ToArray());
    }

    /// <summary>
    /// Parsira order array poruke: [0, "on", ORDER_ARRAY] ili [0, "os", [ORDER_ARRAY, ...]]
    /// </summary>
    private void HandleOrderArray(JsonElement arr)
    {
        if (arr.GetArrayLength() < 2)
        {
            _log.Debug("[BFX-PWS] Order array premali: len={Len}", arr.GetArrayLength());
            return;
        }

        // chanId (obično 0 za private channel)
        var chanId = arr[0].ValueKind == JsonValueKind.Number && arr[0].TryGetInt64(out var cid) ? cid : -1;

        // Event type: "on", "ou", "oc", "os"
        var eventType = arr[1].ValueKind == JsonValueKind.String ? arr[1].GetString() : null;

        if (string.IsNullOrWhiteSpace(eventType))
        {
            _log.Debug("[BFX-PWS] Order array bez event type");
            return;
        }

        switch (eventType)
        {
            case "on": // Order new
                if (arr.GetArrayLength() >= 3 && arr[2].ValueKind == JsonValueKind.Array)
                {
                    BitfinexOrderEvent? order = ParseOrderArray(arr[2]);
                    if (order != null)
                    {
                        OrderNew?.Invoke(order);
                        _log.Information("[BFX-PWS] Order NEW: id={Id} symbol={Sym} type={Type} side={Side} qty={Qty} px={Px}",
                            order.OrderId, order.NativeSymbol, order.OrderType, order.Side, order.Quantity, order.Price);
                    }
                }
                break;

            case "ou": // Order update
                if (arr.GetArrayLength() >= 3 && arr[2].ValueKind == JsonValueKind.Array)
                {
                    var order = ParseOrderArray(arr[2]);
                    if (order != null)
                    {
                        OrderUpdate?.Invoke(order);
                        _log.Information("[BFX-PWS] Order UPDATE: id={Id} symbol={Sym} filled={Filled}/{Qty}",
                            order.OrderId, order.NativeSymbol, order.FilledQuantity, order.Quantity);
                    }
                }
                break;

            case "oc": // Order cancel
                if (arr.GetArrayLength() >= 3 && arr[2].ValueKind == JsonValueKind.Array)
                {
                    var order = ParseOrderArray(arr[2]);
                    if (order != null)
                    {
                        // Loguj status i razlog ako postoji
                        var statusInfo = !string.IsNullOrWhiteSpace(order.Status) 
                            ? $" status={order.Status}" 
                            : "";
                        _log.Information("[BFX-PWS] Order CANCEL: id={Id} symbol={Sym}{Status} qty={Qty} price={Price}",
                            order.OrderId, order.NativeSymbol, statusInfo, order.Quantity, order.Price);
                        OrderCancel?.Invoke(order);
                    }
                }
                break;

            case "os": // Order snapshot (array of orders)
                if (arr.GetArrayLength() >= 3 && arr[2].ValueKind == JsonValueKind.Array)
                {
                    var orders = new List<BitfinexOrderEvent>();
                    foreach (var orderEl in arr[2].EnumerateArray())
                    {
                        if (orderEl.ValueKind == JsonValueKind.Array)
                        {
                            var order = ParseOrderArray(orderEl);
                            if (order != null)
                                orders.Add(order);
                        }
                    }
                    if (orders.Count > 0)
                    {
                        OrderSnapshot?.Invoke(orders.ToArray());
                        _log.Information("[BFX-PWS] Order SNAPSHOT: {Count} orders", orders.Count);
                    }
                }
                break;

            default:
                _log.Debug("[BFX-PWS] Unknown order event type: {Type}", eventType);
                break;
        }
    }

    /// <summary>
    /// Parsira Bitfinex ORDER_ARRAY format (v2).
    /// Indices: 0=ID, 3=SYMBOL, 6=AMOUNT_REM, 7=AMOUNT_ORIG, 8=TYPE, 13=STATUS, 16=PRICE
    /// </summary>
    private BitfinexOrderEvent? ParseOrderArray(JsonElement orderArr)
    {
        if (orderArr.ValueKind != JsonValueKind.Array || orderArr.GetArrayLength() < 18)
            return null;

        try
        {
            var id = GetLongSafe(orderArr, 0);
            if (id <= 0)
                return null;

            var nativeSymbol = GetStringSafe(orderArr, 3);
            if (string.IsNullOrWhiteSpace(nativeSymbol))
                return null;

            var amountRem = GetDecimalSafe(orderArr, 6) ?? 0m;
            var amountOrig = GetDecimalSafe(orderArr, 7) ?? 0m;
            var orderType = GetStringSafe(orderArr, 8) ?? string.Empty;
            var price = GetDecimalSafe(orderArr, 16) ?? 0m;
            var status = GetStringSafe(orderArr, 13) ?? string.Empty;

            var side = amountOrig >= 0m ? CryptoOrderSide.Buy : CryptoOrderSide.Sell;
            var qty = Math.Abs(amountOrig);
            var rem = Math.Abs(amountRem);
            var filled = Math.Max(0m, qty - rem);

            return new BitfinexOrderEvent(
                orderId: id.ToString(CultureInfo.InvariantCulture),
                nativeSymbol: nativeSymbol!,
                orderType: orderType,
                side: side,
                quantity: qty,
                filledQuantity: filled,
                price: price,
                status: status
            );
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[BFX-PWS] Failed to parse order array");
            return null;
        }
    }

    private static long GetLongSafe(JsonElement arr, int index)
    {
        if (index >= arr.GetArrayLength())
            return 0;
        var el = arr[index];
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v))
            return v;
        return 0;
    }

    private static string? GetStringSafe(JsonElement arr, int index)
    {
        if (index >= arr.GetArrayLength())
            return null;
        var el = arr[index];
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static decimal? GetDecimalSafe(JsonElement arr, int index)
    {
        if (index >= arr.GetArrayLength())
            return null;
        var el = arr[index];
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d))
            return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds))
            return ds;
        return null;
    }

    private static string Trunc(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws != null)
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None).ConfigureAwait(false);

                _ws.Dispose();
            }
        }
        catch
        {
            // ignore
        }
    }
}
