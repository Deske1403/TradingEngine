#nullable enable
using System;
using System.Collections.Concurrent;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bybit;

public sealed class BybitMarketDataFeed : IMarketDataFeed
{
    private readonly BybitWebSocketFeed _ws;
    private readonly ILogger _log;

    // Key: PublicSymbol (npr BTCUSD/BTCUSDT kako ti već mapiraš)
    private readonly ConcurrentDictionary<string, (CryptoSymbol Meta, Symbol EngineSymbol)> _byPublic =
        new(StringComparer.OrdinalIgnoreCase);

    // Zadnji top-of-book size iz orderbook.1 (ako ticker size nije popunjen)
    private readonly ConcurrentDictionary<string, (decimal? BidSize, decimal? AskSize)> _lastTopSizes =
        new(StringComparer.OrdinalIgnoreCase);

    public BybitMarketDataFeed(BybitWebSocketFeed ws, ILogger log)
    {
        _ws = ws;
        _log = log.ForContext<BybitMarketDataFeed>();

        _ws.TickerUpdated += OnTicker;
        _ws.OrderBookUpdated += OnOrderBook;
    }

    public event Action<MarketQuote>? MarketQuoteUpdated;

    public void AddSymbol(CryptoSymbol symbol)
    {
        // Engine Symbol: ticker = PublicSymbol, currency = QuoteAsset, exchange = ExchangeId.ToString()
        var engineSymbol = new Symbol(symbol.PublicSymbol, symbol.QuoteAsset, symbol.ExchangeId.ToString());
        _byPublic[symbol.PublicSymbol] = (symbol, engineSymbol);
    }

    public void SubscribeQuotes(Symbol symbol)
    {
        // Program.cs već radi WS subscribe; ovde samo pratimo šablon kao kod ostalih.
        _log.Information("[BYBIT-MD] SubscribeQuotes: {Sym}", symbol.Ticker);
    }

    public void UnsubscribeQuotes(Symbol symbol)
    {
        throw new NotImplementedException();
    }

    private void OnOrderBook(OrderBookUpdate ob)
    {
        // orderbook.1 => ob.Bids/Asks imaju 0 ili 1 nivo; uzmi prvi size
        decimal? bidSize = null;
        decimal? askSize = null;

        if (ob.Bids.Count > 0)
            bidSize = ob.Bids[0].Quantity;

        if (ob.Asks.Count > 0)
            askSize = ob.Asks[0].Quantity;

        if (bidSize.HasValue || askSize.HasValue)
            _lastTopSizes[ob.Symbol.PublicSymbol] = (bidSize, askSize);
    }

    private void OnTicker(TickerUpdate t)
    {
        if (!_byPublic.TryGetValue(t.Symbol.PublicSymbol, out var entry))
            return;

        var bid = t.Bid;
        var ask = t.Ask;
        var last = t.Last;

        // Ako baš nema ništa korisno, preskoči
        if (!bid.HasValue && !ask.HasValue && !last.HasValue)
            return;

        var bidSize = t.BidSize;
        var askSize = t.AskSize;

        // Dopuni size iz last orderbook top-a ako ticker nema size
        if ((!bidSize.HasValue || !askSize.HasValue) && _lastTopSizes.TryGetValue(entry.Meta.PublicSymbol, out var top))
        {
            bidSize ??= top.BidSize;
            askSize ??= top.AskSize;
        }

        var quote = new MarketQuote(
            Symbol: entry.EngineSymbol,
            Bid: bid,
            Ask: ask,
            Last: last,
            BidSize: bidSize,
            AskSize: askSize,
            TimestampUtc: t.TimestampUtc);

        MarketQuoteUpdated?.Invoke(quote);
    }
}