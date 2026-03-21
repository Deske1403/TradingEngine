#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Core.Crypto;
using Serilog;

namespace Denis.TradingEngine.Data.Runtime;

/// <summary>
/// Servis koji automatski snima orderbook update-e iz crypto WebSocket feed-ova u bazu.
/// Koristi BoundedOrderBookQueue za batch insert performanse.
/// </summary>
public sealed class CryptoOrderBookService : IDisposable
{
    private readonly BoundedOrderBookQueue _queue;
    private readonly ILogger _log;
    private readonly List<IOrderBookFeed> _subscribedFeeds = new();
    private readonly ConcurrentDictionary<string, OrderBookUpdate> _latestUsableOrderBooks = new(StringComparer.OrdinalIgnoreCase);
    
    /// <summary>
    /// Event koji se poziva kada se orderbook update primi (pre enqueue-a).
    /// Koristi se za statistike.
    /// </summary>
    public event Action<OrderBookUpdate>? OrderBookReceived;

    public CryptoOrderBookService(
        IDbConnectionFactory dbFactory,
        int queueCapacity = 1000,
        int batchSize = 50,
        TimeSpan? maxBatchDelay = null,
        ILogger? log = null)
    {
        _log = log ?? Log.ForContext<CryptoOrderBookService>();
        
        var orderBookRepo = new CryptoOrderBookRepository(dbFactory, _log);
        
        _queue = new BoundedOrderBookQueue(
            capacity: queueCapacity,
            orderBookRepo: orderBookRepo,
            onOrderBookAsync: null, // može se dodati custom handler ako treba
            batchSize: batchSize,
            maxBatchDelay: maxBatchDelay ?? TimeSpan.FromMilliseconds(1000),
            log: _log);
    }

    /// <summary>
    /// Subscribe-uje se na orderbook update-e iz datog WebSocket feed-a.
    /// </summary>
    public void Subscribe(IOrderBookFeed feed)
    {
        if (feed == null)
            throw new ArgumentNullException(nameof(feed));

        if (_subscribedFeeds.Contains(feed))
        {
            _log.Warning("[CRYPTO-OB-SVC] Feed {Ex} već subscribed", feed.ExchangeId);
            return;
        }

        feed.OrderBookUpdated += OnOrderBookUpdated;
        _subscribedFeeds.Add(feed);
        
        _log.Information("[CRYPTO-OB-SVC] Subscribed to {Ex} orderbook updates", feed.ExchangeId);
    }

    /// <summary>
    /// Unsubscribe-uje se od orderbook update-a iz datog feed-a.
    /// </summary>
    public void Unsubscribe(IOrderBookFeed feed)
    {
        if (feed == null)
            return;

        if (!_subscribedFeeds.Remove(feed))
            return;

        feed.OrderBookUpdated -= OnOrderBookUpdated;
        _log.Information("[CRYPTO-OB-SVC] Unsubscribed from {Ex} orderbook updates", feed.ExchangeId);
    }

    private void OnOrderBookUpdated(OrderBookUpdate update)
    {
        // Fire event za statistike
        OrderBookReceived?.Invoke(update);

        if (update.Bids.Count > 0 && update.Asks.Count > 0)
        {
            _latestUsableOrderBooks[GetCacheKey(update.Symbol.ExchangeId, update.Symbol.PublicSymbol)] = update;
        }
        
        if (!_queue.TryEnqueue(update))
        {
            _log.Warning("[CRYPTO-OB-SVC] Failed to enqueue orderbook update for {Ex}:{Sym}", 
                update.Symbol.ExchangeId, update.Symbol.PublicSymbol);
        }
    }

    public bool TryGetLatestUsableOrderBook(CryptoExchangeId exchangeId, string publicSymbol, out OrderBookUpdate update)
    {
        if (string.IsNullOrWhiteSpace(publicSymbol))
        {
            update = default!;
            return false;
        }

        return _latestUsableOrderBooks.TryGetValue(GetCacheKey(exchangeId, publicSymbol), out update!);
    }

    private static string GetCacheKey(CryptoExchangeId exchangeId, string publicSymbol)
    {
        return $"{exchangeId}:{publicSymbol}";
    }

    public void Dispose()
    {
        // Unsubscribe od svih feed-ova
        foreach (var feed in _subscribedFeeds)
        {
            try
            {
                feed.OrderBookUpdated -= OnOrderBookUpdated;
            }
            catch
            {
                // ignore
            }
        }
        _subscribedFeeds.Clear();

        _queue?.Dispose();
    }
}

