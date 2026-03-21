#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Kraken;

/// <summary>
/// Adapter između KrakenWebSocketFeed i IMarketDataFeed interfejsa.
/// Pretvara Kraken TickerUpdate u MarketQuote za tvoj engine.
/// </summary>
public sealed class KrakenMarketDataFeed : IMarketDataFeed
{
    private readonly KrakenWebSocketFeed _ws;
    private readonly ILogger _log;

    private readonly object _sync = new();

    // "BTCUSD" -> CryptoSymbol metadata
    private readonly Dictionary<string, CryptoSymbol> _symbols =
        new(StringComparer.OrdinalIgnoreCase);
    
    // Cache poslednjeg orderbook-a po simbolu (za bid/ask size)
    private readonly Dictionary<string, OrderBookUpdate> _lastOrderBook = 
        new(StringComparer.OrdinalIgnoreCase);

    public event Action<MarketQuote>? MarketQuoteUpdated;

    public KrakenMarketDataFeed(KrakenWebSocketFeed ws, ILogger log)
    {
        _ws = ws ?? throw new ArgumentNullException(nameof(ws));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _ws.TickerUpdated += OnTickerUpdated;
        _ws.OrderBookUpdated += OnOrderBookUpdated;
    }
    
    private void OnOrderBookUpdated(OrderBookUpdate ob)
    {
        lock (_sync)
        {
            _lastOrderBook[ob.Symbol.PublicSymbol] = ob;
        }
    }

    public void AddSymbol(CryptoSymbol meta)
    {
        if (meta == null) throw new ArgumentNullException(nameof(meta));

        lock (_sync)
        {
            _symbols[meta.PublicSymbol] = meta;
        }
    }

    public void SubscribeQuotes(Symbol symbol)
    {
        CryptoSymbol crypto;

        lock (_sync)
        {
            if (!_symbols.TryGetValue(symbol.Ticker, out crypto!))
            {
                _log.Error("[KRAKEN-FEED] Symbol {Ticker} nije pronađen u metadata.", symbol.Ticker);
                return;
            }
        }

        _log.Information(
            "[KRAKEN-FEED] Subscribujem ticker za {Ticker} ({Native})",
            crypto.PublicSymbol,
            crypto.NativeSymbol);

        _ = _ws.SubscribeTickerAsync(crypto, CancellationToken.None).ContinueWith(t =>
        {
            _log.Error(t.Exception, "[KRAKEN-FEED] SubscribeTickerAsync failed for {Ticker}", crypto.PublicSymbol);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public void UnsubscribeQuotes(Symbol symbol)
    {
        // Kraken NEMA unsubscribe (WS v1) – ignorišemo
    }

    private void OnTickerUpdated(TickerUpdate t)
    {
        // Pokušaj da uzmeš bid/ask size iz orderbook-a ako nema u ticker-u
        decimal? bidSize = t.BidSize;
        decimal? askSize = t.AskSize;
        
        if (!bidSize.HasValue || !askSize.HasValue)
        {
            lock (_sync)
            {
                if (_lastOrderBook.TryGetValue(t.Symbol.PublicSymbol, out var ob))
                {
                    // Uzmi prvi bid/ask size iz orderbook-a
                    if (!bidSize.HasValue && ob.Bids.Count > 0)
                    {
                        bidSize = ob.Bids[0].Quantity;
                    }
                    if (!askSize.HasValue && ob.Asks.Count > 0)
                    {
                        askSize = ob.Asks[0].Quantity;
                    }
                }
            }
        }
        
        var q = new MarketQuote(
            Symbol: new Symbol(
                Ticker: t.Symbol.PublicSymbol,
                Currency: t.Symbol.QuoteAsset,
                Exchange: t.Symbol.ExchangeId.ToString()
            ),
            Bid: t.Bid,
            Ask: t.Ask,
            Last: t.Last,
            BidSize: bidSize,
            AskSize: askSize,
            TimestampUtc: t.TimestampUtc
        );

        try
        {
            MarketQuoteUpdated?.Invoke(q);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[KRAKEN-FEED] MarketQuoteUpdated handler threw for {Ticker}", q.Symbol.Ticker);
        }
    }
}
