#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Exchange.Crypto.Common;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Kraken;

/// <summary>
/// WebSocket feed za Kraken SPOT tržište.
/// - pamti subscribe zahteve i automatski ih ponavlja posle reconnect-a
/// - prima event poruke (OBJECT) i market data poruke (ARRAY)
/// </summary>
public sealed class KrakenWebSocketFeed : WebSocketConnectionBase, ICryptoWebSocketFeed
{
    private readonly ILogger _log;

    // Native Kraken pair (npr. "XBT/USDT") -> CryptoSymbol
    private readonly ConcurrentDictionary<string, CryptoSymbol> _symbolsByPair =
        new(StringComparer.OrdinalIgnoreCase);

    // Desired subscriptions (keys are native pair strings, e.g. "XBT/USDT")
    private readonly ConcurrentDictionary<string, byte> _tickerPairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _tradePairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _bookPairs = new(StringComparer.OrdinalIgnoreCase);

    public event Action<OrderBookUpdate>? OrderBookUpdated;
    public event Action<TradeTick>? TradeReceived;
    public event Action<TickerUpdate>? TickerUpdated;

    public CryptoExchangeId ExchangeId => CryptoExchangeId.Kraken;

    public KrakenWebSocketFeed(string wsUrl, ILogger log)
        : base(wsUrl, log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // -------------------------------------------------------
    //  Reconnect hook: resubscribe everything we remember
    // -------------------------------------------------------
    protected override async Task OnConnectedAsync(CancellationToken ct)
    {
        // Kraken: send subscribe messages again for any previously requested subs
        foreach (var pair in _tickerPairs.Keys)
        {
            await SendSubscribeSafeAsync(KrakenSubscriptionModels.BuildTickerSubscribe(pair), "ticker", pair, ct)
                .ConfigureAwait(false);
        }

        foreach (var pair in _tradePairs.Keys)
        {
            await SendSubscribeSafeAsync(KrakenSubscriptionModels.BuildTradesSubscribe(pair), "trade", pair, ct)
                .ConfigureAwait(false);
        }

        foreach (var pair in _bookPairs.Keys)
        {
            await SendSubscribeSafeAsync(KrakenSubscriptionModels.BuildOrderBookSubscribe(pair), "book", pair, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task SendSubscribeSafeAsync(string json, string kind, string pair, CancellationToken ct)
    {
        try
        {
            _log.Information("[KRAKEN-WS] (re)subscribe {Kind} pair={Pair}", kind, pair);
            await SendAsync(json, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error(ex, "[KRAKEN-WS] Failed to (re)subscribe {Kind} pair={Pair}", kind, pair);
        }
    }

    // -------------------------------------------------------
    //  Incoming messages
    // -------------------------------------------------------
    protected override Task HandleMessageAsync(string rawJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                HandleEventMessage(root);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                HandleArrayMessage(root);
            }
            else
            {
                _log.Debug("[KRAKEN-WS] Nepoznat root tip: {Kind}", root.ValueKind);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[KRAKEN-WS] Greška pri parsiranju WS poruke.");
        }

        return Task.CompletedTask;
    }

    private void HandleEventMessage(JsonElement obj)
    {
        if (!obj.TryGetProperty("event", out var evtProp))
        {
            _log.Debug("[KRAKEN-WS] OBJECT bez 'event' polja, ignorišem.");
            return;
        }

        var evt = evtProp.GetString();

        switch (evt)
        {
            case "heartbeat":
                _log.Debug("[KRAKEN-WS] heartbeat");
                break;

            case "subscriptionStatus":
                {
                    var status = obj.TryGetProperty("status", out var st) ? st.GetString() : "?";
                    var pair = obj.TryGetProperty("pair", out var p) ? p.GetString() : "?";
                    var name = obj.TryGetProperty("subscription", out var subObj)
                               && subObj.TryGetProperty("name", out var n)
                        ? n.GetString()
                        : "?";

                    var err = obj.TryGetProperty("errorMessage", out var e) ? e.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(err))
                    {
                        _log.Warning("[KRAKEN-WS] subscriptionStatus: {Status} {Name} {Pair} err={Err}",
                            status, name, pair, err);
                    }
                    else
                    {
                        _log.Information("[KRAKEN-WS] subscriptionStatus: {Status} {Name} {Pair} (FullJson={Json})",
                            status, name, pair, obj.ToString());
                    }
                    break;
                }

            case "systemStatus":
                _log.Information("[KRAKEN-WS] systemStatus: {Json}", obj.ToString());
                break;

            default:
                _log.Debug("[KRAKEN-WS] event={Event}: {Json}", evt, obj.ToString());
                break;
        }
    }

    private void HandleArrayMessage(JsonElement arr)
    {
        // Expected:
        // [channelId, <data>, "ticker", "XBT/USDT"]
        if (arr.GetArrayLength() < 4)
        {
            _log.Debug("[KRAKEN-WS] ARRAY sa premalo elemenata: {Len}", arr.GetArrayLength());
            return;
        }

        var channelNameElem = arr[2];
        if (channelNameElem.ValueKind != JsonValueKind.String)
        {
            _log.Debug("[KRAKEN-WS] ARRAY[2] nije string (channelName), ignorišem.");
            return;
        }

        var channelName = channelNameElem.GetString();
        var pairElem = arr[3];
        var pair = pairElem.ValueKind == JsonValueKind.String ? pairElem.GetString() : null;

        if (string.IsNullOrWhiteSpace(channelName) || string.IsNullOrWhiteSpace(pair))
        {
            _log.Debug("[KRAKEN-WS] ARRAY bez validnog channelName/pair.");
            return;
        }

        switch (channelName)
        {
            case "ticker":
                HandleTickerArrayMessage(arr[1], pair);
                break;

            case "trade":
                HandleTradeArrayMessage(arr[1], pair);
                break;

            case "book-10":
            case "book-25":
            case "book-100":
                HandleOrderBookArrayMessage(arr[1], pair);
                break;

            default:
                _log.Debug("[KRAKEN-WS] Nepoznat channelName={Channel}", channelName);
                break;
        }
    }

    private void HandleTickerArrayMessage(JsonElement tickerObj, string pair)
    {
        if (tickerObj.ValueKind != JsonValueKind.Object)
        {
            _log.Debug("[KRAKEN-WS] ticker data nije OBJECT.");
            return;
        }

        if (!_symbolsByPair.TryGetValue(pair, out var symbol))
        {
            _log.Debug("[KRAKEN-WS] ticker za nepoznat pair={Pair} (nema ga u _symbolsByPair).", pair);
            return;
        }

        decimal? bid = null;
        decimal? ask = null;
        decimal? last = null;
        decimal? volume24h = null;

        if (tickerObj.TryGetProperty("b", out var bArr) &&
            bArr.ValueKind == JsonValueKind.Array &&
            bArr.GetArrayLength() > 0)
        {
            bid = TryGetDecimalFromString(bArr[0]);
        }

        if (tickerObj.TryGetProperty("a", out var aArr) &&
            aArr.ValueKind == JsonValueKind.Array &&
            aArr.GetArrayLength() > 0)
        {
            ask = TryGetDecimalFromString(aArr[0]);
        }

        if (tickerObj.TryGetProperty("c", out var cArr) &&
            cArr.ValueKind == JsonValueKind.Array &&
            cArr.GetArrayLength() > 0)
        {
            last = TryGetDecimalFromString(cArr[0]);
        }

        if (tickerObj.TryGetProperty("v", out var vArr) &&
            vArr.ValueKind == JsonValueKind.Array &&
            vArr.GetArrayLength() > 1)
        {
            volume24h = TryGetDecimalFromString(vArr[1]);
        }

        var update = new TickerUpdate(
            Symbol: symbol,
            TimestampUtc: DateTime.UtcNow,
            Bid: bid,
            Ask: ask,
            Last: last,
            Volume24h: volume24h);

        TickerUpdated?.Invoke(update);
    }

    private void HandleTradeArrayMessage(JsonElement tradesElem, string pair)
    {
        if (tradesElem.ValueKind != JsonValueKind.Array || tradesElem.GetArrayLength() == 0)
        {
            _log.Debug("[KRAKEN-WS] trade message za pair={Pair}, ali nema elemenata.", pair);
            return;
        }

        if (!_symbolsByPair.TryGetValue(pair, out var symbol))
        {
            _log.Debug("[KRAKEN-WS] trade za nepoznat pair={Pair} (nema ga u _symbolsByPair).", pair);
            return;
        }

        for (int i = 0; i < tradesElem.GetArrayLength(); i++)
        {
            var t = tradesElem[i];
            if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() < 6)
                continue;

            var price = TryGetDecimalFromString(t[0]) ?? 0m;
            var qty = TryGetDecimalFromString(t[1]) ?? 0m;

            var timeStr = t[2].GetString(); // unix seconds (fractional)
            var sideStr = t[3].GetString(); // "b" or "s"

            DateTime tsUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(timeStr) &&
                double.TryParse(timeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
            {
                tsUtc = DateTime.UnixEpoch.AddSeconds(seconds);
            }

            var side = sideStr switch
            {
                "b" => TradeSide.Buy,
                "s" => TradeSide.Sell,
                _ => TradeSide.Buy
            };

            var tick = new TradeTick(
                Symbol: symbol,
                TimestampUtc: tsUtc,
                Price: price,
                Quantity: qty,
                Side: side);

            TradeReceived?.Invoke(tick);
        }
    }

    // -------------------------------------------------------
    //  Subscriptions
    // -------------------------------------------------------
    public async Task SubscribeOrderBookAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);
        _bookPairs.TryAdd(symbol.NativeSymbol, 0);

        if (!IsConnected)
        {
            _log.Information("[KRAKEN-WS] SubscribeOrderBookAsync: NOT CONNECTED, will subscribe after connect. Symbol={Symbol}", symbol);
            return;
        }

        var msg = KrakenSubscriptionModels.BuildOrderBookSubscribe(symbol.NativeSymbol);
        _log.Information("[KRAKEN-WS] SubscribeOrderBookAsync: Sending subscription for {Symbol}, msg={Msg}", symbol, msg);
        await SendAsync(msg, ct).ConfigureAwait(false);
    }

    public Task SubscribeTradesAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);
        _tradePairs.TryAdd(symbol.NativeSymbol, 0);

        if (!IsConnected)
            return Task.CompletedTask;

        var msg = KrakenSubscriptionModels.BuildTradesSubscribe(symbol.NativeSymbol);
        return SendAsync(msg, ct);
    }

    public Task SubscribeTickerAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);
        _tickerPairs.TryAdd(symbol.NativeSymbol, 0);

        if (!IsConnected)
            return Task.CompletedTask;

        var msg = KrakenSubscriptionModels.BuildTickerSubscribe(symbol.NativeSymbol);
        return SendAsync(msg, ct);
    }

    private void RememberSymbol(CryptoSymbol symbol)
    {
        if (!string.IsNullOrWhiteSpace(symbol.NativeSymbol))
        {
            _symbolsByPair[symbol.NativeSymbol] = symbol;
        }
    }

    private void HandleOrderBookArrayMessage(JsonElement bookObj, string pair)
    {
        if (bookObj.ValueKind != JsonValueKind.Object)
        {
            _log.Warning("[KRAKEN-WS] book data nije OBJECT. ValueKind={Kind}, Json={Json}", bookObj.ValueKind, bookObj.ToString());
            return;
        }

        if (!_symbolsByPair.TryGetValue(pair, out var symbol))
        {
            _log.Warning("[KRAKEN-WS] book za nepoznat pair={Pair} (nema ga u _symbolsByPair). Known pairs: {Pairs}", 
                pair, string.Join(", ", _symbolsByPair.Keys));
            return;
        }

        var hasBs = bookObj.TryGetProperty("bs", out var _);
        var hasAs = bookObj.TryGetProperty("as", out var _);
        var hasBids = bookObj.TryGetProperty("bids", out var _);
        var hasAsks = bookObj.TryGetProperty("asks", out var _);
        var hasB = bookObj.TryGetProperty("b", out var _);
        var hasA = bookObj.TryGetProperty("a", out var _);

        var bids = new List<(decimal Price, decimal Quantity)>();
        var asks = new List<(decimal Price, decimal Quantity)>();
        bool asksProcessed = false;
        bool bidsProcessed = false;

        // Kraken v1 format može biti:
        // - Snapshot: "bs" (bids) i "as" (asks) - array of [price, qty, timestamp]
        // - Update: "b" (bids) i "a" (asks) - array of [price, qty, timestamp]
        // - Alternativno: "bids" i "asks" (možda u nekim verzijama)
        
        // Prvo proveravamo standardni v1 snapshot format (bs/as)
        if (bookObj.TryGetProperty("bs", out var bsArr) && bsArr.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < bsArr.GetArrayLength(); i++)
            {
                var level = bsArr[i];
                if (level.ValueKind == JsonValueKind.Array && level.GetArrayLength() >= 2)
                {
                    var price = TryGetDecimalFromString(level[0]);
                    var qty = TryGetDecimalFromString(level[1]);
                    if (price.HasValue && qty.HasValue && qty.Value > 0)
                    {
                        bids.Add((price.Value, qty.Value));
                    }
                }
            }
            bidsProcessed = true;
        }

        // Proveravamo snapshot format (as) ili update format (a)
        if (bookObj.TryGetProperty("as", out var asArr) && asArr.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < asArr.GetArrayLength(); i++)
            {
                var level = asArr[i];
                if (level.ValueKind == JsonValueKind.Array && level.GetArrayLength() >= 2)
                {
                    var price = TryGetDecimalFromString(level[0]);
                    var qty = TryGetDecimalFromString(level[1]);
                    if (price.HasValue && qty.HasValue && qty.Value > 0)
                    {
                        asks.Add((price.Value, qty.Value));
                    }
                }
            }
            asksProcessed = true;
        }
        else if (bookObj.TryGetProperty("a", out var aArrUpdate) && aArrUpdate.ValueKind == JsonValueKind.Array)
        {
            // Update format: "a" = asks (delta update)
            for (int i = 0; i < aArrUpdate.GetArrayLength(); i++)
            {
                var level = aArrUpdate[i];
                if (level.ValueKind == JsonValueKind.Array && level.GetArrayLength() >= 2)
                {
                    var price = TryGetDecimalFromString(level[0]);
                    var qty = TryGetDecimalFromString(level[1]);
                    // qty=0 znači da se level briše, qty>0 znači da se dodaje/update-uje
                    if (price.HasValue && qty.HasValue)
                    {
                        if (qty.Value > 0)
                        {
                            asks.Add((price.Value, qty.Value));
                        }
                        // qty=0 se ignoriše (brisanje level-a)
                    }
                }
            }
            asksProcessed = true;
        }
        else if (hasAsks && bookObj.TryGetProperty("asks", out var asksArr) && asksArr.ValueKind == JsonValueKind.Array)
        {
            // Alternativni format: "asks" array of objects {price, qty}
            for (int i = 0; i < asksArr.GetArrayLength(); i++)
            {
                var level = asksArr[i];
                if (level.ValueKind == JsonValueKind.Object)
                {
                    var price = TryGetDecimal(level, "price");
                    var qty = TryGetDecimal(level, "qty");
                    if (price.HasValue && qty.HasValue && qty.Value > 0)
                    {
                        asks.Add((price.Value, qty.Value));
                    }
                }
            }
            asksProcessed = true;
        }

        if (!asksProcessed)
        {
            // Nema asks u ovoj poruci - to je OK, možda su samo bids update ili asks dolaze u drugoj poruci
            _log.Debug("[KRAKEN-WS] No 'as'/'a'/'asks' property in this message. HasAs={HasAs}, HasAsks={HasAsks}", 
                hasAs, hasAsks);
        }

        // Proveravamo bids format
        if (hasBids && bookObj.TryGetProperty("bids", out var bidsArr) && bidsArr.ValueKind == JsonValueKind.Array)
        {
            // Alternativni format: "bids" array of objects {price, qty}
            for (int i = 0; i < bidsArr.GetArrayLength(); i++)
            {
                var level = bidsArr[i];
                if (level.ValueKind == JsonValueKind.Object)
                {
                    var price = TryGetDecimal(level, "price");
                    var qty = TryGetDecimal(level, "qty");
                    if (price.HasValue && qty.HasValue && qty.Value > 0)
                    {
                        bids.Add((price.Value, qty.Value));
                    }
                }
            }
            bidsProcessed = true;
        }
        else if (bookObj.TryGetProperty("b", out var bArr) && bArr.ValueKind == JsonValueKind.Array)
        {
            // Update format: "b" = bids
            for (int i = 0; i < bArr.GetArrayLength(); i++)
            {
                var level = bArr[i];
                if (level.ValueKind == JsonValueKind.Array && level.GetArrayLength() >= 2)
                {
                    var price = TryGetDecimalFromString(level[0]);
                    var qty = TryGetDecimalFromString(level[1]);
                    if (price.HasValue && qty.HasValue && qty.Value > 0)
                    {
                        bids.Add((price.Value, qty.Value));
                    }
                }
            }
            bidsProcessed = true;
        }

        if (!bidsProcessed)
        {
            // Nema bids u ovoj poruci - to je OK, možda su samo asks update ili bids dolaze u drugoj poruci
            _log.Debug("[KRAKEN-WS] No 'bs'/'b'/'bids' property in this message. HasBs={HasBs}, HasBids={HasBids}", 
                bookObj.TryGetProperty("bs", out var _), hasBids);
        }

        if (bids.Count > 0 || asks.Count > 0)
        {
            var update = new OrderBookUpdate(
                Symbol: symbol,
                TimestampUtc: DateTime.UtcNow,
                Bids: bids,
                Asks: asks);

            OrderBookUpdated?.Invoke(update);
        }
        else
        {
            _log.Debug("[KRAKEN-WS] OrderBookUpdate skipped: no bids/asks. pair={Pair}", pair);
        }
    }

    private static decimal? TryGetDecimalFromString(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.String)
            return null;

        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    private static decimal? TryGetDecimal(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var d))
            return d;

        if (prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            if (!string.IsNullOrWhiteSpace(s) && 
                decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2))
                return d2;
        }

        return null;
    }
}
