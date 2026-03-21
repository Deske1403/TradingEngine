#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto;

/// <summary>
/// In-memory rolling count po (exchange, symbol) + batch insert u crypto_trades.
/// Implementira IActivityTicksProvider – odluka ide iz memorije, bez poziva baze.
/// </summary>
public sealed class CryptoTradeTicksBatchWriter : IActivityTicksProvider
{
    private readonly CryptoTradesRepository _repo;
    private readonly string _exchangeName;
    private readonly ILogger _log;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly object _sync = new();
    private static readonly TimeSpan MaxWindowRetain = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TradeIdDedupeRetain = TimeSpan.FromMinutes(10);

    // (exchange, symbol) -> queue of tick timestamps (rolling window)
    private readonly Dictionary<(string, string), Queue<DateTime>> _ticksByKey = new();
    // (exchange, symbol, tradeId) -> first seen (wall clock) for te/tu + reconnect overlap dedupe
    private readonly Dictionary<(string, string, long), DateTime> _seenTradeIds = new();
    private readonly Queue<((string, string, long) Key, DateTime SeenUtc)> _seenTradeIdsEviction = new();
    private readonly List<CryptoTradeRecord> _buffer = new();
    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private CancellationTokenSource? _flushCts;
    private Task? _flushTask;
    private long _tradeEventsAccepted;
    private long _tradeEventsDeduped;

    public CryptoTradeTicksBatchWriter(
        CryptoTradesRepository repo,
        string exchangeName,
        ILogger? log = null,
        int batchSize = 200,
        int flushIntervalSeconds = 2)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _exchangeName = exchangeName ?? throw new ArgumentNullException(nameof(exchangeName));
        _log = log ?? Log.ForContext<CryptoTradeTicksBatchWriter>();
        _batchSize = batchSize > 0 ? batchSize : 200;
        _flushInterval = TimeSpan.FromSeconds(flushIntervalSeconds > 0 ? flushIntervalSeconds : 2);
    }

    /// <summary>
    /// Dodaj trade (iz TradeReceived). Ažurira in-memory count i buffer za DB.
    /// </summary>
    public void Add(TradeTick tick)
    {
        if (tick.Symbol == null) return;

        var symbol = tick.Symbol.PublicSymbol;
        if (string.IsNullOrEmpty(symbol)) return;

        var key = (_exchangeName, symbol);
        var utc = tick.TimestampUtc == default ? DateTime.UtcNow : tick.TimestampUtc;
        var nowUtc = DateTime.UtcNow;
        var side = tick.Side == TradeSide.Buy ? "buy" : "sell";

        lock (_sync)
        {
            PruneSeenTradeIdsLocked(nowUtc);

            if (tick.TradeId is long tradeId)
            {
                var tradeKey = (_exchangeName, symbol, tradeId);
                if (_seenTradeIds.ContainsKey(tradeKey))
                {
                    _tradeEventsDeduped++;
                    return;
                }

                _seenTradeIds[tradeKey] = nowUtc;
                _seenTradeIdsEviction.Enqueue((tradeKey, nowUtc));
            }

            if (!_ticksByKey.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTime>();
                _ticksByKey[key] = queue;
            }
            var cutoff = utc - MaxWindowRetain;
            while (queue.Count > 0 && queue.Peek() < cutoff)
                queue.Dequeue();
            queue.Enqueue(utc);
            _tradeEventsAccepted++;

            _buffer.Add(new CryptoTradeRecord(
                utc,
                _exchangeName,
                symbol,
                tick.Price,
                tick.Quantity,
                side,
                tick.TradeId
            ));
        }
    }

    private void PruneSeenTradeIdsLocked(DateTime nowUtc)
    {
        var cutoff = nowUtc - TradeIdDedupeRetain;
        while (_seenTradeIdsEviction.Count > 0)
        {
            var (tradeKey, seenUtc) = _seenTradeIdsEviction.Peek();
            if (seenUtc >= cutoff)
                break;

            _seenTradeIdsEviction.Dequeue();

            if (_seenTradeIds.TryGetValue(tradeKey, out var currentSeenUtc) && currentSeenUtc == seenUtc)
                _seenTradeIds.Remove(tradeKey);
        }
    }

    /// <summary>
    /// Broj tikova u prozoru – iz memorije, bez DB.
    /// </summary>
    public int GetTicksInWindow(string exchange, string symbol, DateTime sinceUtc)
    {
        var key = (exchange, symbol);
        lock (_sync)
        {
            if (!_ticksByKey.TryGetValue(key, out var queue))
                return 0;
            while (queue.Count > 0 && queue.Peek() < sinceUtc)
                queue.Dequeue();
            return queue.Count;
        }
    }

    /// <summary>
    /// Pokreće periodični flush (po interval).
    /// </summary>
    public void StartFlushLoop(CancellationToken ct)
    {
        if (_flushTask != null) return;

        _flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linked = _flushCts.Token;
        _flushTask = Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_flushInterval, linked).ConfigureAwait(false);
                    await FlushAsync(linked).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[CRYPTO-TRADES] flush loop error");
                }
            }
        }, linked);
        _log.Information("[CRYPTO-TRADES] Batch writer started exchange={Exchange} batchSize={Batch} intervalSec={Sec}",
            _exchangeName, _batchSize, _flushInterval.TotalSeconds);
    }

    /// <summary>
    /// Flush buffer u bazu (po batch size ili na zahtev).
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        List<CryptoTradeRecord> toFlush;
        long accepted;
        long deduped;
        int seenCache;
        lock (_sync)
        {
            if (_buffer.Count == 0) return;
            toFlush = new List<CryptoTradeRecord>(_buffer);
            _buffer.Clear();
            _lastFlushUtc = DateTime.UtcNow;
            accepted = _tradeEventsAccepted;
            deduped = _tradeEventsDeduped;
            seenCache = _seenTradeIds.Count;
        }
        await _repo.InsertBatchAsync(toFlush, ct).ConfigureAwait(false);

        if (deduped > 0)
        {
            _log.Debug("[CRYPTO-TRADES] Dedupe stats exchange={Exchange} accepted={Accepted} deduped={Deduped} seenCache={SeenCache}",
                _exchangeName, accepted, deduped, seenCache);
        }
    }

    /// <summary>
    /// Flush ako je buffer pun ili je prošao interval.
    /// Poziva se i iz Add ako buffer.Count >= _batchSize (opciono) – trenutno samo timer.
    /// </summary>
    public async Task FlushIfNeededAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_buffer.Count == 0) return;
            if (_buffer.Count < _batchSize && (DateTime.UtcNow - _lastFlushUtc) < _flushInterval)
                return;
        }
        await FlushAsync(ct).ConfigureAwait(false);
    }

    public void StopFlushLoop()
    {
        _flushCts?.Cancel();
        _flushTask = null;
    }
}
