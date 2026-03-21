#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Data.Repositories;
using Serilog;

namespace Denis.TradingEngine.Data.Runtime
{
    /// <summary>
    /// Bounded tick queue:
    /// - više writer-a (feed-ovi),
    /// - jedan consumer worker,
    /// - batch insert u market_ticks (off IBKR thread),
    /// - drop-uje NAJSTARIJE tickove kad se napuni (DropOldest).
    /// </summary>
    public sealed class BoundedTickQueue : IDisposable
    {
        private readonly Channel<MarketQuote> _channel;
        private readonly ILogger _log;
        private readonly MarketTickRepository? _tickRepo;
        private readonly Func<MarketQuote, Task> _onTickAsync;
        private readonly int _capacity;
        private readonly int _batchSize;
        private readonly TimeSpan _maxBatchDelay;
        private long _approxQueueDepth;
        private long _estimatedDroppedTicks;

        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopTask;

        public BoundedTickQueue(
            int capacity,
            Func<MarketQuote, Task> onTickAsync,
            MarketTickRepository? tickRepo,
            int batchSize = 100,
            TimeSpan? maxBatchDelay = null,
            ILogger? log = null)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _onTickAsync = onTickAsync ?? throw new ArgumentNullException(nameof(onTickAsync));
            _tickRepo = tickRepo;
            _capacity = capacity;
            _batchSize = batchSize > 0 ? batchSize : 100;
            _maxBatchDelay = maxBatchDelay ?? TimeSpan.FromMilliseconds(500);

            var opts = new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            };

            _channel = Channel.CreateBounded<MarketQuote>(opts);
            _log = log ?? Log.ForContext<BoundedTickQueue>();

            _loopTask = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var buffer = new List<MarketQuote>(_batchSize);
            var lastFlushUtc = DateTime.UtcNow;

            try
            {
                while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (_channel.Reader.TryRead(out var q))
                    {
                        var depthAfterRead = Interlocked.Decrement(ref _approxQueueDepth);
                        if (depthAfterRead < 0)
                            Interlocked.Exchange(ref _approxQueueDepth, 0);
                        // 1) Strategija / orchestrator logika (ako je prosleđena)
                        try
                        {
                            await _onTickAsync(q).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning(ex, "[TICK-QUEUE] onTickAsync handler failed for {Sym}", q.Symbol.Ticker);
                        }

                        // 2) DB batch buffer
                        if (_tickRepo is not null)
                        {
                            buffer.Add(q);
                        }

                        var now = DateTime.UtcNow;

                        if (_tickRepo is not null &&
                            (buffer.Count >= _batchSize || (now - lastFlushUtc) >= _maxBatchDelay))
                        {
                            await FlushBatchAsync(buffer, ct).ConfigureAwait(false);
                            lastFlushUtc = now;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normalan shutdown
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[TICK-QUEUE] RunAsync crashed");
            }

            // finalni flush
            if (_tickRepo is not null && buffer.Count > 0)
            {
                try
                {
                    await FlushBatchAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[TICK-QUEUE] Final flush failed");
                }
            }
        }

        private async Task FlushBatchAsync(List<MarketQuote> buffer, CancellationToken ct)
        {
            if (_tickRepo is null || buffer.Count == 0)
                return;

            var snapshot = buffer.ToArray();
            buffer.Clear();

            try
            {
                await _tickRepo.BatchInsertAsync(snapshot, ct).ConfigureAwait(false);
                _log.Debug("[DB-TICKS] Flushed batch count={Count}", snapshot.Length);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DB-TICKS] batch insert failed (count={Count})", snapshot.Length);
            }
        }


        /// <summary>
        /// Non-blocking enqueue; zbog FullMode=DropOldest praktično
        /// vraća false samo ako je queue zatvoren / disposing.
        /// </summary>
        public bool TryEnqueue(MarketQuote tick)
        {
            if (_cts.IsCancellationRequested)
                return false;

            if (!_channel.Writer.TryWrite(tick))
                return false;

            var depthAfterWrite = Interlocked.Increment(ref _approxQueueDepth);
            if (depthAfterWrite > _capacity)
            {
                // DropOldest: kanal primi novi tick i izbaci najstariji.
                Interlocked.Decrement(ref _approxQueueDepth);

                var droppedTotal = Interlocked.Increment(ref _estimatedDroppedTicks);
                if (droppedTotal == 1 || (droppedTotal % 100) == 0)
                {
                    _log.Warning(
                        "[TICK-QUEUE] Queue saturated (DropOldest active) cap={Cap} estDropped={Dropped} depth~={Depth} ex={Ex} sym={Sym}",
                        _capacity,
                        droppedTotal,
                        Math.Min(Volatile.Read(ref _approxQueueDepth), _capacity),
                        tick.Symbol.Exchange,
                        tick.Symbol.Ticker);
                }
            }

            return true;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _loopTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }

            var dropped = Volatile.Read(ref _estimatedDroppedTicks);
            if (dropped > 0)
            {
                _log.Warning("[TICK-QUEUE] Shutdown summary estDropped={Dropped}", dropped);
            }
        }
    }
}
