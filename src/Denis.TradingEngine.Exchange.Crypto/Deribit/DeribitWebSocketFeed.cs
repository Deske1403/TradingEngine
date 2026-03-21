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

namespace Denis.TradingEngine.Exchange.Crypto.Deribit;

/// <summary>
/// WebSocket feed za Deribit.
/// - pamti subscribe zahteve i automatski ih ponavlja posle reconnect-a
/// - parsi "subscription" poruke i emituje TickerUpdate
/// </summary>
public sealed class DeribitWebSocketFeed : WebSocketConnectionBase, ICryptoWebSocketFeed
{
    private readonly ILogger _log;

    // instrument_name (npr. "BTC-PERPETUAL") -> CryptoSymbol
    private readonly ConcurrentDictionary<string, CryptoSymbol> _symbolsByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    private long _nextId = 9;

    // request_id -> response waiter
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pendingRequests =
        new();

    // desired subscriptions (channels)
    private readonly ConcurrentDictionary<string, byte> _tickerChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _bookChannels = new(StringComparer.OrdinalIgnoreCase);

    public CryptoExchangeId ExchangeId => CryptoExchangeId.Deribit;

    public event Action<OrderBookUpdate>? OrderBookUpdated;
    public event Action<TradeTick>? TradeReceived;
    public event Action<TickerUpdate>? TickerUpdated;

    public DeribitWebSocketFeed(string wsUrl, ILogger log)
        : base(wsUrl, log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // -------------------------------------------------------
    //  Reconnect hook: resubscribe remembered channels
    // -------------------------------------------------------
    protected override async Task OnConnectedAsync(CancellationToken ct)
    {
        // svi pending requesti od prethodne konekcije više ne važe
        foreach (var kv in _pendingRequests)
        {
            kv.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();

        foreach (var ch in _tickerChannels.Keys)
        {
            await SubscribeChannelInternalAsync(ch, ct).ConfigureAwait(false);
        }

        foreach (var ch in _bookChannels.Keys)
        {
            await SubscribeChannelInternalAsync(ch, ct).ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------
    //  ICryptoWebSocketFeed
    // -------------------------------------------------------
    public async Task SubscribeOrderBookAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);

        // Deribit orderbook channel: book.{instrument_name}.{group}.{depth}.{interval}
        // group = "none" (raw), depth = "10" (10 levels), interval = "100ms"
        var channel = $"book.{symbol.NativeSymbol}.none.10.100ms";
        _bookChannels.TryAdd(channel, 0);

        if (!IsConnected)
        {
            _log.Information("[DERIBIT-WS] SubscribeOrderBookAsync: NOT CONNECTED, will subscribe after connect. Symbol={Symbol}, Channel={Channel}", symbol, channel);
            return;
        }

        _log.Information("[DERIBIT-WS] SubscribeOrderBookAsync: Sending subscription for {Symbol}, channel={Channel}", symbol, channel);
        await SubscribeChannelInternalAsync(channel, ct).ConfigureAwait(false);
    }

    public Task SubscribeTradesAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);
        // TODO: implement trades
        return Task.CompletedTask;
    }

    public async Task SubscribeTickerAsync(CryptoSymbol symbol, CancellationToken ct)
    {
        RememberSymbol(symbol);

        var channel = $"ticker.{symbol.NativeSymbol}.100ms";
        _tickerChannels.TryAdd(channel, 0);

        if (!IsConnected)
            return;

        await SubscribeChannelInternalAsync(channel, ct).ConfigureAwait(false);
    }

    private void RememberSymbol(CryptoSymbol symbol)
    {
        if (!string.IsNullOrWhiteSpace(symbol.NativeSymbol))
        {
            _symbolsByInstrument[symbol.NativeSymbol] = symbol;
            _log.Information("[DERIBIT-MD] RememberSymbol {Symbol} (instrument={Instrument})",
                symbol, symbol.NativeSymbol);
        }
    }

    private async Task SubscribeChannelInternalAsync(string channel, CancellationToken ct)
    {
        var id = (int)Interlocked.Increment(ref _nextId);

        var msg = new
        {
            jsonrpc = "2.0",
            id = id,
            method = "public/subscribe",
            @params = new
            {
                channels = new[] { channel }
            }
        };

        var json = JsonSerializer.Serialize(msg);
        _log.Information("[DERIBIT-WS] (re)subscribe ticker channel={Channel} id={Id}", channel, id);

        await SendAsync(json, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------
    //  WS message handling
    // -------------------------------------------------------
    protected override Task HandleMessageAsync(string rawJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return Task.CompletedTask;

            // 0) Response na request (ima "id")
            if (root.TryGetProperty("id", out var idElem) &&
                idElem.ValueKind == JsonValueKind.Number)
            {
                var id = idElem.GetInt32();

                if (_pendingRequests.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(root.Clone()); // Clone da preživi posle using
                    return Task.CompletedTask;
                }
            }

            // 1) method-based poruke (subscription, heartbeat...)
            if (root.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString();

                switch (method)
                {
                    case "subscription":
                        HandleSubscription(root);
                        break;

                    case "heartbeat":
                        _log.Debug("[DERIBIT-WS] heartbeat");
                        break;

                    default:
                        _log.Debug("[DERIBIT-WS] method={Method}: {Json}", method, rawJson);
                        break;
                }

                return Task.CompletedTask;
            }

            // 2) result bez pending match-a
            if (root.TryGetProperty("result", out var _))
            {
                _log.Debug("[DERIBIT-WS] response: {Json}", rawJson);
                return Task.CompletedTask;
            }

            _log.Debug("[DERIBIT-WS] Nepoznata poruka: {Json}", rawJson);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[DERIBIT-WS] Greška pri parsiranju WS poruke.");
        }

        return Task.CompletedTask;
    }

    private void HandleSubscription(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var paramsElem) ||
            paramsElem.ValueKind != JsonValueKind.Object)
        {
            _log.Debug("[DERIBIT-WS] subscription bez params");
            return;
        }

        if (!paramsElem.TryGetProperty("channel", out var chElem))
        {
            _log.Debug("[DERIBIT-WS] subscription bez channel");
            return;
        }

        var channel = chElem.GetString();
        if (string.IsNullOrWhiteSpace(channel))
        {
            _log.Debug("[DERIBIT-WS] prazno ime kanala u subscription");
            return;
        }

        if (!paramsElem.TryGetProperty("data", out var dataElem) ||
            dataElem.ValueKind != JsonValueKind.Object)
        {
            _log.Debug("[DERIBIT-WS] subscription bez data objekta");
            return;
        }

        if (channel.StartsWith("ticker.", StringComparison.Ordinal))
        {
            HandleTicker(channel, dataElem);
        }
        else if (channel.StartsWith("book.", StringComparison.Ordinal))
        {
            HandleOrderBook(channel, dataElem);
        }
        else
        {
            _log.Debug("[DERIBIT-WS] subscription za nepoznat kanal {Channel}", channel);
        }
    }

    private void HandleTicker(string channel, JsonElement data)
    {
        if (!data.TryGetProperty("instrument_name", out var instElem))
        {
            _log.Debug("[DERIBIT-WS] ticker bez instrument_name: {Json}", data.ToString());
            return;
        }

        var instrument = instElem.GetString();
        if (string.IsNullOrWhiteSpace(instrument))
            return;

        if (!_symbolsByInstrument.TryGetValue(instrument, out var symbol))
        {
            _log.Debug("[DERIBIT-WS] ticker za nepoznat instrument={Instrument}", instrument);
            return;
        }

        decimal? bid = TryGetDecimal(data, "best_bid_price");
        decimal? ask = TryGetDecimal(data, "best_ask_price");
        decimal? last = TryGetDecimal(data, "last_price");
        decimal? vol24h = null;

        if (data.TryGetProperty("stats", out var statsElem) &&
            statsElem.ValueKind == JsonValueKind.Object)
        {
            vol24h = TryGetDecimal(statsElem, "volume");
        }

        var update = new TickerUpdate(
            Symbol: symbol,
            TimestampUtc: DateTime.UtcNow,
            Bid: bid,
            Ask: ask,
            Last: last,
            Volume24h: vol24h
        );

        TickerUpdated?.Invoke(update);
    }

    private void HandleOrderBook(string channel, JsonElement data)
    {
        if (!data.TryGetProperty("instrument_name", out var instElem))
        {
            _log.Debug("[DERIBIT-WS] orderbook bez instrument_name: {Json}", data.ToString());
            return;
        }

        var instrument = instElem.GetString();
        if (string.IsNullOrWhiteSpace(instrument))
            return;

        if (!_symbolsByInstrument.TryGetValue(instrument, out var symbol))
        {
            _log.Debug("[DERIBIT-WS] orderbook za nepoznat instrument={Instrument}", instrument);
            return;
        }

        var bids = new List<(decimal Price, decimal Quantity)>();
        var asks = new List<(decimal Price, decimal Quantity)>();

        // Deribit format: bids/asks su array-ovi [price, size, ...]
        if (data.TryGetProperty("bids", out var bidsElem) && bidsElem.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < bidsElem.GetArrayLength(); i++)
            {
                var level = bidsElem[i];
                if (level.ValueKind == JsonValueKind.Array && level.GetArrayLength() >= 2)
                {
                    var price = TryGetDecimal(level, 0);
                    var size = TryGetDecimal(level, 1);
                    if (price.HasValue && size.HasValue && size.Value > 0)
                    {
                        bids.Add((price.Value, size.Value));
                    }
                }
            }
        }

        if (data.TryGetProperty("asks", out var asksElem) && asksElem.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < asksElem.GetArrayLength(); i++)
            {
                var level = asksElem[i];
                if (level.ValueKind == JsonValueKind.Array && level.GetArrayLength() >= 2)
                {
                    var price = TryGetDecimal(level, 0);
                    var size = TryGetDecimal(level, 1);
                    if (price.HasValue && size.HasValue && size.Value > 0)
                    {
                        asks.Add((price.Value, size.Value));
                    }
                }
            }
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

    private static decimal? TryGetDecimal(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
            return null;

        try
        {
            return prop.ValueKind switch
            {
                JsonValueKind.Number => prop.GetDecimal(),
                JsonValueKind.String => decimal.TryParse(
                    prop.GetString(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var d
                )
                    ? d
                    : null,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------
    //  Request/response helper (za DeribitTradingApi)
    // -------------------------------------------------------
    public Task<JsonElement?> WaitResponseAsync(int id, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonElement?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(id, tcs))
            throw new InvalidOperationException($"Request ID already pending: {id}");

        ct.Register(() => tcs.TrySetCanceled(ct), useSynchronizationContext: false);

        return tcs.Task;
    }
}
