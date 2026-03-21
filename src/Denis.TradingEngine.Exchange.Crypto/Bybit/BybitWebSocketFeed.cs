#nullable enable
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Exchange.Crypto.Common;
using Serilog;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Denis.TradingEngine.Exchange.Crypto.Bybit;

public sealed class BybitWebSocketFeed : WebSocketConnectionBase, ICryptoWebSocketFeed
{
    private readonly ILogger _log;

    // nativeSymbol (Bybit) -> CryptoSymbol (naš)
    private readonly ConcurrentDictionary<string, CryptoSymbol> _symbolsByNative = new();

    // "šta je user tražio da se subscribuje" (da možemo resubscribe posle reconnect-a)
    private readonly ConcurrentDictionary<string, byte> _tickerNative = new();
    private readonly ConcurrentDictionary<string, byte> _tradesNative = new();
    private readonly ConcurrentDictionary<string, byte> _bookNative = new();

    private CancellationTokenSource? _pingCts;

    public CryptoExchangeId ExchangeId => CryptoExchangeId.Bybit;
    
    public event Action<TickerUpdate>? TickerUpdated;
    public event Action<TradeTick>? TradeReceived;
    public event Action<OrderBookUpdate>? OrderBookUpdated;

    public BybitWebSocketFeed(string wsUrl, ILogger log)
        : base(wsUrl, log)
    {
        _log = log.ForContext<BybitWebSocketFeed>();
    }

    protected override async Task OnConnectedAsync(CancellationToken ct)
    {
        await base.OnConnectedAsync(ct).ConfigureAwait(false);

        _log.Information("[BYBIT-WS] Connected. Resubscribing...");

        // start ping loop
        _pingCts?.Cancel();
        _pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => PingLoopAsync(_pingCts.Token), _pingCts.Token);

        // resubscribe sve što je traženo pre connect-a
        var args = new List<string>();

        foreach (var kv in _tickerNative.Keys)
            args.Add($"tickers.{kv}");

        foreach (var kv in _tradesNative.Keys)
            args.Add($"publicTrade.{kv}");

        foreach (var kv in _bookNative.Keys)
            args.Add($"orderbook.1.{kv}");

        if (args.Count > 0)
        {
            var sub = BuildSubscribe(args);
            _log.Information("[BYBIT-WS] Resubscribe: {Json}", sub);
            await SendAsync(sub, ct).ConfigureAwait(false);
        }
    }
    protected override Task HandleClosedAsync(System.Net.WebSockets.WebSocketCloseStatus? status, string? description)
    {
        try
        {
            _pingCts?.Cancel();
        }
        catch
        {
        }

        return base.HandleClosedAsync(status, description);
    }


    private async Task PingLoopAsync(CancellationToken ct)
    {
        // Bybit preporučuje ping ~20s :contentReference[oaicite:5]{index=5}
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    break;

                if (IsConnected)
                {
                    var ping = "{\"op\":\"ping\"}";
                    await SendAsync(ping, ct).ConfigureAwait(false);
                    _log.Debug("[BYBIT-WS] ping sent");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[BYBIT-WS] ping loop error");
            }
        }
    }

    // -------------------------------------------------------
    //  Subscriptions (kao Kraken/Bitfinex pattern)
    // -------------------------------------------------------
    public Task SubscribeTickerAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);
        _tickerNative.TryAdd(symbol.NativeSymbol, 0);

        if (!IsConnected)
            return Task.CompletedTask;

        var json = BuildSubscribe(new[] { $"tickers.{symbol.NativeSymbol}" });
        _log.Information("[BYBIT-WS] Subscribe ticker: {Json}", json);
        return SendAsync(json, ct);
    }

    public Task SubscribeTradesAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);
        _tradesNative.TryAdd(symbol.NativeSymbol, 0);

        if (!IsConnected)
            return Task.CompletedTask;

        var json = BuildSubscribe(new[] { $"publicTrade.{symbol.NativeSymbol}" });
        _log.Information("[BYBIT-WS] Subscribe trades: {Json}", json);
        return SendAsync(json, ct);
    }

    public Task SubscribeOrderBookAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);
        _bookNative.TryAdd(symbol.NativeSymbol, 0);

        if (!IsConnected)
        {
            _log.Information("[BYBIT-WS] SubscribeOrderBookAsync: NOT CONNECTED, will subscribe after connect. Symbol={Symbol}", symbol);
            return Task.CompletedTask;
        }

        // za start: orderbook.1 (top-of-book). Kasnije možemo 50/200.
        var json = BuildSubscribe(new[] { $"orderbook.1.{symbol.NativeSymbol}" });
        _log.Information("[BYBIT-WS] Subscribe orderbook: {Json}", json);
        return SendAsync(json, ct);
    }

    private void RememberSymbol(CryptoSymbol symbol)
    {
        if (!string.IsNullOrWhiteSpace(symbol.NativeSymbol))
            _symbolsByNative[symbol.NativeSymbol] = symbol;
    }

    private static string BuildSubscribe(IEnumerable<string> args)
    {
        // Bybit subscribe: {"op":"subscribe","args":[...]} :contentReference[oaicite:6]{index=6}
        return JsonSerializer.Serialize(new { op = "subscribe", args = args.ToArray() });
    }

    // -------------------------------------------------------
    //  Message handling
    // -------------------------------------------------------
    protected override Task HandleMessageAsync(string raw, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // command responses (subscribe/ping/pong/auth)
            if (root.TryGetProperty("op", out var opProp))
            {
                var op = opProp.GetString();
                if (!string.IsNullOrWhiteSpace(op))
                {
                    _log.Debug("[BYBIT-WS] Command msg op={Op} raw={Raw}", op, raw);
                    return Task.CompletedTask;
                }
            }

            if (!root.TryGetProperty("topic", out var topicProp) || topicProp.ValueKind != JsonValueKind.String)
            {
                _log.Debug("[BYBIT-WS] No topic. raw={Raw}", raw);
                return Task.CompletedTask;
            }

            var topic = topicProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(topic))
                return Task.CompletedTask;

            // tickers.{symbol} :contentReference[oaicite:7]{index=7}
            if (topic.StartsWith("tickers.", StringComparison.OrdinalIgnoreCase))
            {
                HandleTicker(root, topic);
                return Task.CompletedTask;
            }

            // publicTrade.{symbol} :contentReference[oaicite:8]{index=8}
            if (topic.StartsWith("publicTrade.", StringComparison.OrdinalIgnoreCase))
            {
                HandleTrades(root, topic);
                return Task.CompletedTask;
            }

            // orderbook.{depth}.{symbol} :contentReference[oaicite:9]{index=9}
            if (topic.StartsWith("orderbook.", StringComparison.OrdinalIgnoreCase))
            {
                HandleOrderBook(root, topic);
                return Task.CompletedTask;
            }

            _log.Debug("[BYBIT-WS] Unhandled topic={Topic} raw={Raw}", topic, raw);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[BYBIT-WS] HandleMessage parse error. raw={Raw}", raw);
            return Task.CompletedTask;
        }
    }

    private void HandleTicker(JsonElement root, string topic)
    {
        if (!root.TryGetProperty("data", out var dataElem))
            return;

        // docs: "data array Object" :contentReference[oaicite:10]{index=10}
        // U praksi često dođe kao object ili array[object] - podržimo oba.
        JsonElement obj;
        if (dataElem.ValueKind == JsonValueKind.Object)
        {
            obj = dataElem;
        }
        else if (dataElem.ValueKind == JsonValueKind.Array && dataElem.GetArrayLength() > 0 && dataElem[0].ValueKind == JsonValueKind.Object)
        {
            obj = dataElem[0];
        }
        else
        {
            _log.Debug("[BYBIT-WS] ticker data unexpected kind={Kind}", dataElem.ValueKind);
            return;
        }

        var native = TryGetString(obj, "symbol");
        if (string.IsNullOrWhiteSpace(native))
            native = TopicSymbol(topic);

        if (string.IsNullOrWhiteSpace(native) || !_symbolsByNative.TryGetValue(native, out var sym))
            return;

        // Bid/Ask/Last
        var bid = TryGetDecimal(obj, "bid1Price");
        var ask = TryGetDecimal(obj, "ask1Price");
        var last = TryGetDecimal(obj, "lastPrice");
        var vol24h = TryGetDecimal(obj, "volume24h");

        var bidSize = TryGetDecimal(obj, "bid1Size");
        var askSize = TryGetDecimal(obj, "ask1Size");

        var tsUtc = DateTime.UtcNow;
        if (root.TryGetProperty("ts", out var tsProp))
        {
            var ms = TryGetLong(tsProp);
            if (ms.HasValue)
                tsUtc = DateTime.UnixEpoch.AddMilliseconds(ms.Value);
        }

        var update = new TickerUpdate(
            Symbol: sym,
            TimestampUtc: tsUtc,
            Bid: bid,
            Ask: ask,
            Last: last,
            Volume24h: vol24h,
            BidSize: bidSize,
            AskSize: askSize
        );

        TickerUpdated?.Invoke(update);
    }

    private void HandleTrades(JsonElement root, string topic)
    {
        if (!root.TryGetProperty("data", out var dataElem) || dataElem.ValueKind != JsonValueKind.Array)
            return;

        // docs: data array sorted by time ascending; fields T,s,S,v,p :contentReference[oaicite:11]{index=11}
        for (int i = 0; i < dataElem.GetArrayLength(); i++)
        {
            var t = dataElem[i];
            if (t.ValueKind != JsonValueKind.Object)
                continue;

            var native = TryGetString(t, "s");
            if (string.IsNullOrWhiteSpace(native))
                native = TopicSymbol(topic);

            if (string.IsNullOrWhiteSpace(native) || !_symbolsByNative.TryGetValue(native, out var sym))
                continue;

            var price = TryGetDecimal(t, "p") ?? 0m;
            var qty = TryGetDecimal(t, "v") ?? 0m;

            var sideStr = TryGetString(t, "S");
            var side = string.Equals(sideStr, "Sell", StringComparison.OrdinalIgnoreCase)
                ? TradeSide.Sell
                : TradeSide.Buy;

            DateTime tsUtc = DateTime.UtcNow;
            if (t.TryGetProperty("T", out var tsProp))
            {
                var ms = TryGetLong(tsProp);
                if (ms.HasValue)
                    tsUtc = DateTime.UnixEpoch.AddMilliseconds(ms.Value);
            }

            if (price <= 0m || qty <= 0m)
                continue;

            var tick = new TradeTick(
                Symbol: sym,
                TimestampUtc: tsUtc,
                Price: price,
                Quantity: qty,
                Side: side
            );

            TradeReceived?.Invoke(tick);
        }
    }

    private void HandleOrderBook(JsonElement root, string topic)
    {
        if (!root.TryGetProperty("data", out var dataElem) || dataElem.ValueKind != JsonValueKind.Object)
            return;

        // docs: data map object: s,b,a,u,seq :contentReference[oaicite:12]{index=12}
        var native = TryGetString(dataElem, "s");
        if (string.IsNullOrWhiteSpace(native))
            native = TopicSymbol(topic);

        if (string.IsNullOrWhiteSpace(native) || !_symbolsByNative.TryGetValue(native, out var sym))
            return;

        var bids = new List<(decimal Price, decimal Quantity)>();
        var asks = new List<(decimal Price, decimal Quantity)>();

        if (dataElem.TryGetProperty("b", out var bArr) && bArr.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < bArr.GetArrayLength(); i++)
            {
                var lvl = bArr[i];
                if (lvl.ValueKind != JsonValueKind.Array || lvl.GetArrayLength() < 2)
                    continue;

                var px = TryGetDecimalFromString(lvl[0]);
                var q = TryGetDecimalFromString(lvl[1]);
                if (px.HasValue && q.HasValue && q.Value > 0)
                    bids.Add((px.Value, q.Value));
            }
        }

        if (dataElem.TryGetProperty("a", out var aArr) && aArr.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < aArr.GetArrayLength(); i++)
            {
                var lvl = aArr[i];
                if (lvl.ValueKind != JsonValueKind.Array || lvl.GetArrayLength() < 2)
                    continue;

                var px = TryGetDecimalFromString(lvl[0]);
                var q = TryGetDecimalFromString(lvl[1]);
                if (px.HasValue && q.HasValue && q.Value > 0)
                    asks.Add((px.Value, q.Value));
            }
        }

        if (bids.Count == 0 && asks.Count == 0)
            return;

        var tsUtc = DateTime.UtcNow;
        if (root.TryGetProperty("ts", out var tsProp))
        {
            var ms = TryGetLong(tsProp);
            if (ms.HasValue)
                tsUtc = DateTime.UnixEpoch.AddMilliseconds(ms.Value);
        }

        var update = new OrderBookUpdate(
            Symbol: sym,
            TimestampUtc: tsUtc,
            Bids: bids,
            Asks: asks
        );

        OrderBookUpdated?.Invoke(update);
    }

    private static string TopicSymbol(string topic)
    {
        var idx = topic.LastIndexOf('.');
        if (idx < 0 || idx + 1 >= topic.Length)
            return topic;

        return topic[(idx + 1)..];
    }

    private static string? TryGetString(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            _ => el.ToString()
        };
    }

    private static long? TryGetLong(JsonElement el)
    {
        try
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
                return n;

            if (el.ValueKind == JsonValueKind.String &&
                long.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n2))
                return n2;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static decimal? TryGetDecimal(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d))
            return d;

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s) &&
                decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2))
                return d2;
        }

        return null;
    }

    private static decimal? TryGetDecimalFromString(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d))
            return d;

        if (el.ValueKind != JsonValueKind.String)
            return null;

        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) ? d2 : null;
    }
}
