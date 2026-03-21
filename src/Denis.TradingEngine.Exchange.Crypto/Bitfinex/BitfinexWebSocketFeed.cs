#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Exchange.Crypto.Common;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex;

/// <summary>
/// WebSocket feed za Bitfinex (public market data).
/// - pamti subscribe zahteve i automatski ih ponavlja posle reconnect-a
/// - chanId se menja posle reconnect-a, zato se map resetuje
/// </summary>
public sealed class BitfinexWebSocketFeed : WebSocketConnectionBase, ICryptoWebSocketFeed
{
    private readonly ILogger _log;

    // chanId -> CryptoSymbol (dodelimo kad dobijemo "subscribed")
    private readonly ConcurrentDictionary<long, CryptoSymbol> _symbolsByChannelId = new();
    private readonly ConcurrentDictionary<long, LocalBookState> _bookStateByChannelId = new();

    // native symbol ("tBTCUSD") -> CryptoSymbol
    private readonly ConcurrentDictionary<string, CryptoSymbol> _symbolsByNative =
        new(StringComparer.OrdinalIgnoreCase);

    // desired subscriptions (ključ je native symbol, npr. "tBTCUSD")
    private readonly ConcurrentDictionary<string, byte> _tickerNative = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _tradesNative = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _bookNative = new(StringComparer.OrdinalIgnoreCase);

    // za ovu konekciju već poslat subscribe (da ne šaljemo dup; brišemo na reconnect)
    private readonly ConcurrentDictionary<string, byte> _tickerSubscribeSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _tradesSubscribeSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _bookSubscribeSent = new(StringComparer.OrdinalIgnoreCase);

    public event Action<OrderBookUpdate>? OrderBookUpdated;
    public event Action<TradeTick>? TradeReceived;
    public event Action<TickerUpdate>? TickerUpdated;

    public CryptoExchangeId ExchangeId => CryptoExchangeId.Bitfinex;

    public BitfinexWebSocketFeed(string wsUrl, ILogger log)
        : base(wsUrl, log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // -------------------------------------------------------
    //  Reconnect hook: resubscribe everything we remember
    // -------------------------------------------------------
    protected override async Task OnConnectedAsync(CancellationToken ct)
    {
        // posle reconnect-a chanId više ne važi; očistimo i "sent" da ponovo pošaljemo subscribe
        _symbolsByChannelId.Clear();
        _bookStateByChannelId.Clear();
        _tickerSubscribeSent.Clear();
        _tradesSubscribeSent.Clear();
        _bookSubscribeSent.Clear();

        foreach (var native in _tickerNative.Keys)
        {
            var json = BuildSubscribeJson(channel: "ticker", nativeSymbol: native);
            await SendSubscribeSafeAsync(json, "ticker", native, ct).ConfigureAwait(false);
            _tickerSubscribeSent.TryAdd(native, 0);
        }

        foreach (var native in _tradesNative.Keys)
        {
            var json = BuildSubscribeJson(channel: "trades", nativeSymbol: native);
            await SendSubscribeSafeAsync(json, "trades", native, ct).ConfigureAwait(false);
            _tradesSubscribeSent.TryAdd(native, 0);
        }

        foreach (var native in _bookNative.Keys)
        {
            var json = BuildSubscribeJson(channel: "book", nativeSymbol: native);
            await SendSubscribeSafeAsync(json, "book", native, ct).ConfigureAwait(false);
            _bookSubscribeSent.TryAdd(native, 0);
        }
    }

    private async Task SendSubscribeSafeAsync(string json, string kind, string native, CancellationToken ct)
    {
        try
        {
            _log.Information("[BITFINEX-WS] (re)subscribe {Kind} {Native}", kind, native);
            await SendAsync(json, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-WS] Failed to (re)subscribe {Kind} {Native}", kind, native);
        }
    }

    private static string BuildSubscribeJson(string channel, string nativeSymbol)
    {
        var msg = new
        {
            @event = "subscribe",
            channel = channel,
            symbol = nativeSymbol
        };

        // Za orderbook, dodajemo prec, freq, len parametre
        if (channel == "book")
        {
            return JsonSerializer.Serialize(new
            {
                @event = "subscribe",
                channel = "book",
                symbol = nativeSymbol,
                prec = "P0",  // Precision: P0 = no aggregation, P1 = 1 decimal, etc.
                freq = "F0",  // Frequency: F0 = real-time, F1 = 1 second updates
                len = "25"    // Length: number of price levels (25, 100, 250)
            });
        }

        return JsonSerializer.Serialize(msg);
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
                _log.Debug("[BITFINEX-WS] Nepoznat root tip: {Kind}", root.ValueKind);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-WS] Greška pri parsiranju WS poruke.");
        }

        return Task.CompletedTask;
    }

    private void HandleEventMessage(JsonElement obj)
    {
        if (!obj.TryGetProperty("event", out var evtProp))
        {
            _log.Debug("[BITFINEX-WS] OBJECT bez 'event' polja, ignorišem.");
            return;
        }

        var evt = evtProp.GetString();

        switch (evt)
        {
            case "info":
                _log.Information("[BITFINEX-WS] info: {Json}", obj.ToString());
                break;

            case "subscribed":
                {
                    var channel = obj.TryGetProperty("channel", out var c) ? c.GetString() : "?";
                    _log.Information("[BITFINEX-WS] subscribed event: channel={Channel}, fullJson={Json}", channel, obj.ToString());
                    HandleSubscribedEvent(obj);
                    break;
                }

            case "error":
                {
                    var msg = obj.TryGetProperty("msg", out var m) ? m.GetString() : "?";
                    // subscribe: dup (code 10301) = ne šaljemo više dup; ostale error ostaju Error
                    if (msg != null && msg.Contains("subscribe: dup", StringComparison.OrdinalIgnoreCase))
                        _log.Warning("[BITFINEX-WS] subscribe dup (ignored): {Msg}", msg);
                    else
                        _log.Error("[BITFINEX-WS] error: {Msg} ({Json})", msg, obj.ToString());
                    break;
                }

            case "pong":
                _log.Debug("[BITFINEX-WS] pong");
                break;

            default:
                _log.Debug("[BITFINEX-WS] event={Event}: {Json}", evt, obj.ToString());
                break;
        }
    }

    private void HandleSubscribedEvent(JsonElement obj)
    {
        var channel = obj.TryGetProperty("channel", out var c) ? c.GetString() : "?";
        var chanId = obj.TryGetProperty("chanId", out var idEl) && idEl.ValueKind == JsonValueKind.Number
            ? idEl.GetInt64()
            : -1;

        var symbolStr = obj.TryGetProperty("symbol", out var symEl) ? symEl.GetString() : null;

        if (chanId <= 0 || string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(symbolStr))
        {
            _log.Information("[BITFINEX-WS] subscribed (nekompletno): {Json}", obj.ToString());
            return;
        }

        if (_symbolsByNative.TryGetValue(symbolStr, out var symbol))
        {
            _symbolsByChannelId[chanId] = symbol;
            _log.Information("[BITFINEX-WS] subscribed channel={Channel} chanId={ChanId} symbol={Symbol} (FullJson={Json})",
                channel, chanId, symbol, obj.ToString());
        }
        else
        {
            _log.Warning("[BITFINEX-WS] subscribed channel={Channel} chanId={ChanId} ali symbol nepoznat: {Native}. Known symbols: {Symbols}",
                channel, chanId, symbolStr, string.Join(", ", _symbolsByNative.Keys));
        }
    }

    private void HandleArrayMessage(JsonElement arr)
    {
        // Bitfinex data format:
        // [ chanId, <payload> ]
        if (arr.GetArrayLength() < 2)
        {
            _log.Debug("[BITFINEX-WS] ARRAY sa premalo elemenata: {Len}", arr.GetArrayLength());
            return;
        }

        var chanIdEl = arr[0];
        if (chanIdEl.ValueKind != JsonValueKind.Number)
        {
            _log.Debug("[BITFINEX-WS] ARRAY[0] nije broj (chanId)");
            return;
        }

        var chanId = chanIdEl.GetInt64();
        var payload = arr[1];
        
        if (!_symbolsByChannelId.ContainsKey(chanId))
        {
            _log.Debug("[BITFINEX-WS] chanId={ChanId} not in map. Known chanIds: {Channels}", 
                chanId, string.Join(", ", _symbolsByChannelId.Keys));
        }

        // Heartbeat: [chanId, "hb"]
        // Trade update (flat): [chanId, "te"|"tu", id, mts, amount, price] – payload je string, podaci u arr[2..5]
        if (payload.ValueKind == JsonValueKind.String)
        {
            var s = payload.GetString();
            if (string.Equals(s, "hb", StringComparison.OrdinalIgnoreCase))
            {
                _log.Debug("[BITFINEX-WS] heartbeat chanId={ChanId}", chanId);
                return;
            }
            // Live trade: [chanId, "te", [id, mts, amount, price]] – trade je array u arr[2]
            if ((string.Equals(s, "te", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "tu", StringComparison.OrdinalIgnoreCase))
                && arr.GetArrayLength() >= 3
                && _symbolsByChannelId.TryGetValue(chanId, out var symbolFlat))
            {
                var tradeArr = arr[2];
                if (tradeArr.ValueKind == JsonValueKind.Array && tradeArr.GetArrayLength() >= 4)
                {
                    var tick = ParseTradeRow(symbolFlat, tradeArr, offset: 0);
                    if (tick != null)
                        TradeReceived?.Invoke(tick);
                }
                return;
            }
            _log.Debug("[BITFINEX-WS] chanId={ChanId}, string payload={Payload}", chanId, s);
            return;
        }

        // Ticker snapshot/update: [chanId, [BID, BID_SIZE, ASK, ASK_SIZE, ...]]
        // Orderbook snapshot: [chanId, [[price, count, amount], ...]] - payload je array of arrays
        // Orderbook update: [chanId, [price, count, amount]] - payload je array of numbers
        if (payload.ValueKind == JsonValueKind.Array)
        {
            if (payload.GetArrayLength() == 0)
            {
                _log.Debug("[BITFINEX-WS] Empty array payload for chanId={ChanId}", chanId);
                return;
            }

            var firstElem = payload[0];
            if (firstElem.ValueKind == JsonValueKind.String)
            {
                // Trade update: ["te"|"tu", id, mts, amount, price]
                HandleTradeUpdate(chanId, payload);
            }
            else if (firstElem.ValueKind == JsonValueKind.Array)
            {
                var innerLen = firstElem.GetArrayLength();
                if (innerLen == 3)
                    HandleOrderBookArray(chanId, payload);
                else if (innerLen >= 4)
                    HandleTradesSnapshot(chanId, payload);
                else
                    HandleOrderBookArray(chanId, payload);
            }
            else if (firstElem.ValueKind == JsonValueKind.Number && payload.GetArrayLength() == 3)
            {
                HandleOrderBookUpdate(chanId, payload);
            }
            else
            {
                HandleTickerArray(chanId, payload);
            }
            return;
        }

        _log.Debug("[BITFINEX-WS] ARRAY chanId={ChanId} sa nepoznatim payload tipom: {Kind}",
            chanId, payload.ValueKind);
    }

    /// <summary>
    /// Ticker payload format:
    /// [ BID, BID_SIZE, ASK, ASK_SIZE, DAILY_CHANGE, DAILY_CHANGE_PERC, LAST_PRICE, VOLUME, HIGH, LOW ]
    /// </summary>
    private void HandleTickerArray(long chanId, JsonElement dataArr)
    {
        if (!_symbolsByChannelId.TryGetValue(chanId, out var symbol))
        {
            _log.Debug("[BITFINEX-WS] ticker za nepoznat chanId={ChanId}", chanId);
            return;
        }

        if (dataArr.GetArrayLength() < 8)
        {
            _log.Debug("[BITFINEX-WS] ticker array premali: len={Len}", dataArr.GetArrayLength());
            return;
        }

        decimal? bid = TryGetDecimal(dataArr, 0);
        decimal? bidSize = TryGetDecimal(dataArr, 1); // BID_SIZE
        decimal? ask = TryGetDecimal(dataArr, 2);
        decimal? askSize = TryGetDecimal(dataArr, 3); // ASK_SIZE
        decimal? last = TryGetDecimal(dataArr, 6);
        decimal? vol24h = TryGetDecimal(dataArr, 7);

        var update = new TickerUpdate(
            Symbol: symbol,
            TimestampUtc: DateTime.UtcNow,
            Bid: bid,
            Ask: ask,
            Last: last,
            Volume24h: vol24h,
            BidSize: bidSize,
            AskSize: askSize
        );

        TickerUpdated?.Invoke(update);
    }

    /// <summary>
    /// Trades snapshot: [chanId, [ [id, mts, amount, price], ... ] ]
    /// </summary>
    private void HandleTradesSnapshot(long chanId, JsonElement payload)
    {
        if (!_symbolsByChannelId.TryGetValue(chanId, out var symbol))
        {
            _log.Debug("[BITFINEX-WS] trades snapshot za nepoznat chanId={ChanId}", chanId);
            return;
        }
        for (int i = 0; i < payload.GetArrayLength(); i++)
        {
            var row = payload[i];
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 4) continue;
            var tick = ParseTradeRow(symbol, row);
            if (tick != null)
                TradeReceived?.Invoke(tick);
        }
    }

    /// <summary>
    /// Trade update: [chanId, [ "te"|"tu", id, mts, amount, price ] ]
    /// </summary>
    private void HandleTradeUpdate(long chanId, JsonElement payload)
    {
        if (!_symbolsByChannelId.TryGetValue(chanId, out var symbol))
        {
            _log.Debug("[BITFINEX-WS] trade update za nepoznat chanId={ChanId}", chanId);
            return;
        }
        if (payload.GetArrayLength() < 5) return;
        var tick = ParseTradeRow(symbol, payload, offset: 1);
        if (tick != null)
            TradeReceived?.Invoke(tick);
    }

    private static TradeTick? ParseTradeRow(CryptoSymbol symbol, JsonElement row, int offset = 0)
    {
        if (offset + 4 > row.GetArrayLength()) return null;
        var idEl = row[offset];
        var mtsEl = row[offset + 1];
        var amountEl = row[offset + 2];
        var priceEl = row[offset + 3];
        long? tradeId = idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var id) ? id : null;
        if (!mtsEl.TryGetInt64(out var mts)) return null;
        if (!amountEl.TryGetDecimal(out var amount)) return null;
        if (!priceEl.TryGetDecimal(out var price)) return null;
        var tsUtc = DateTime.UnixEpoch.AddMilliseconds(mts);
        var side = amount >= 0 ? TradeSide.Buy : TradeSide.Sell;
        var qty = Math.Abs(amount);
        return new TradeTick(symbol, tsUtc, price, qty, side, tradeId);
    }

    private void HandleOrderBookArray(long chanId, JsonElement dataArr)
    {
        if (!_symbolsByChannelId.TryGetValue(chanId, out var symbol))
        {
            _log.Warning("[BITFINEX-WS] orderbook za nepoznat chanId={ChanId}. Known channelIds: {Channels}", 
                chanId, string.Join(", ", _symbolsByChannelId.Keys));
            return;
        }

        var state = _bookStateByChannelId.GetOrAdd(chanId, _ => new LocalBookState());

        // Bitfinex snapshot format: [[price, count, amount], ...]
        // amount > 0 = bid, amount < 0 = ask (abs(amount) je količina)
        lock (state.Sync)
        {
            state.Bids.Clear();
            state.Asks.Clear();

        for (int i = 0; i < dataArr.GetArrayLength(); i++)
        {
            var level = dataArr[i];
            if (level.ValueKind != JsonValueKind.Array || level.GetArrayLength() < 3)
                continue;

            var price = TryGetDecimal(level, 0);
            var count = TryGetDecimal(level, 1);
            var amount = TryGetDecimal(level, 2);

            if (!price.HasValue || !amount.HasValue)
                continue;

            // count = 0 znači da se level briše
            if (count.HasValue && count.Value == 0)
                continue;

            // Ako je amount > 0, to je bid; ako je < 0, to je ask
            if (amount.Value > 0)
            {
                state.Bids[price.Value] = amount.Value;
            }
            else if (amount.Value < 0)
            {
                state.Asks[price.Value] = Math.Abs(amount.Value);
            }
        }

            EmitMergedOrderBook(chanId, symbol, state);
        }
    }

    private void HandleOrderBookUpdate(long chanId, JsonElement dataArr)
    {
        if (!_symbolsByChannelId.TryGetValue(chanId, out var symbol))
        {
            _log.Warning("[BITFINEX-WS] orderbook update za nepoznat chanId={ChanId}. Known channelIds: {Channels}", 
                chanId, string.Join(", ", _symbolsByChannelId.Keys));
            return;
        }

        var state = _bookStateByChannelId.GetOrAdd(chanId, _ => new LocalBookState());

        // Bitfinex update format: [price, count, amount]
        if (dataArr.GetArrayLength() < 3)
        {
            _log.Warning("[BITFINEX-WS] orderbook update premali: len={Len}", dataArr.GetArrayLength());
            return;
        }

        var price = TryGetDecimal(dataArr, 0);
        var count = TryGetDecimal(dataArr, 1);
        var amount = TryGetDecimal(dataArr, 2);

        if (!price.HasValue || !count.HasValue || !amount.HasValue)
        {
            _log.Warning("[BITFINEX-WS] orderbook update invalid values: price={Price}, count={Count}, amount={Amount}", 
                price, count, amount);
            return;
        }

        lock (state.Sync)
        {
            ApplyDeltaLevel(state, price.Value, count.Value, amount.Value);
            EmitMergedOrderBook(chanId, symbol, state);
        }
    }

    private void ApplyDeltaLevel(LocalBookState state, decimal price, decimal count, decimal amount)
    {
        if (amount > 0)
        {
            if (count > 0)
            {
                state.Bids[price] = amount;
            }
            else
            {
                state.Bids.Remove(price);
            }

            state.Asks.Remove(price);
            return;
        }

        if (amount < 0)
        {
            if (count > 0)
            {
                state.Asks[price] = Math.Abs(amount);
            }
            else
            {
                state.Asks.Remove(price);
            }

            state.Bids.Remove(price);
        }
    }

    private void EmitMergedOrderBook(long chanId, CryptoSymbol symbol, LocalBookState state)
    {
        if (state.Bids.Count == 0 || state.Asks.Count == 0)
        {
            _log.Debug("[BITFINEX-WS] Merged orderbook incomplete for chanId={ChanId}: bids={BidCount} asks={AskCount}",
                chanId, state.Bids.Count, state.Asks.Count);
            return;
        }

        var bids = state.Bids
            .Reverse()
            .Select(kvp => (Price: kvp.Key, Quantity: kvp.Value))
            .Take(25)
            .ToArray();
        var asks = state.Asks
            .Select(kvp => (Price: kvp.Key, Quantity: kvp.Value))
            .Take(25)
            .ToArray();

        if (bids.Length == 0 || asks.Length == 0)
        {
            _log.Debug("[BITFINEX-WS] OrderBookUpdate skipped after merge: no usable bids/asks. chanId={ChanId}", chanId);
            return;
        }

        var update = new OrderBookUpdate(
            Symbol: symbol,
            TimestampUtc: DateTime.UtcNow,
            Bids: bids,
            Asks: asks);

        OrderBookUpdated?.Invoke(update);
    }

    private static decimal? TryGetDecimal(JsonElement arr, int index)
    {
        if (index >= arr.GetArrayLength())
            return null;

        var el = arr[index];
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d))
            return d;

        return null;
    }

    // -------------------------------------------------------
    //  Subscriptions
    // -------------------------------------------------------
    public async Task SubscribeOrderBookAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);
        _bookNative.TryAdd(symbol.NativeSymbol, 0);

        if (!IsConnected)
        {
            _log.Information("[BITFINEX-WS] SubscribeOrderBookAsync: NOT CONNECTED, will subscribe after connect. Symbol={Symbol}", symbol);
            return;
        }
        if (!_bookSubscribeSent.TryAdd(symbol.NativeSymbol, 0))
        {
            _log.Debug("[BITFINEX-WS] SubscribeOrderBookAsync: already subscribed {Symbol}, skip", symbol);
            return;
        }

        var json = BuildSubscribeJson("book", symbol.NativeSymbol);
        _log.Information("[BITFINEX-WS] SubscribeOrderBookAsync: Sending subscription for {Symbol}, json={Json}", symbol, json);
        await SendAsync(json, ct).ConfigureAwait(false);
    }

    public Task SubscribeTradesAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);
        _tradesNative.TryAdd(symbol.NativeSymbol, 0);

        if (!IsConnected)
            return Task.CompletedTask;
        if (!_tradesSubscribeSent.TryAdd(symbol.NativeSymbol, 0))
        {
            _log.Debug("[BITFINEX-WS] SubscribeTradesAsync: already subscribed {Symbol}, skip", symbol);
            return Task.CompletedTask;
        }

        var json = BuildSubscribeJson("trades", symbol.NativeSymbol);
        _log.Information("[BITFINEX-WS] Šaljem subscribe trades: {Json}", json);
        return SendAsync(json, ct);
    }

    public Task SubscribeTickerAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);
        _tickerNative.TryAdd(symbol.NativeSymbol, 0);

        if (!IsConnected)
            return Task.CompletedTask;
        if (!_tickerSubscribeSent.TryAdd(symbol.NativeSymbol, 0))
        {
            _log.Debug("[BITFINEX-WS] SubscribeTickerAsync: already subscribed {Symbol}, skip", symbol);
            return Task.CompletedTask;
        }

        var json = BuildSubscribeJson("ticker", symbol.NativeSymbol);
        _log.Information("[BITFINEX-WS] Šaljem subscribe ticker: {Json}", json);
        return SendAsync(json, ct);
    }

    private void RememberSymbol(CryptoSymbol symbol)
    {
        if (!string.IsNullOrWhiteSpace(symbol.NativeSymbol))
        {
            _symbolsByNative[symbol.NativeSymbol] = symbol;
        }
    }

    private sealed class LocalBookState
    {
        public object Sync { get; } = new();
        public SortedDictionary<decimal, decimal> Bids { get; } = new();
        public SortedDictionary<decimal, decimal> Asks { get; } = new();
    }
}
