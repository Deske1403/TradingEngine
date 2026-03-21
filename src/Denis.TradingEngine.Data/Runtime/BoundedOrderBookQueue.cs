#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Core.Crypto;
using Serilog;

namespace Denis.TradingEngine.Data.Runtime;

/// <summary>
/// Bounded orderbook queue:
/// - više writer-a (crypto WebSocket feed-ovi),
/// - jedan consumer worker,
/// - batch insert u crypto_orderbooks (off WebSocket thread),
/// - drop-uje NAJSTARIJE orderbook update-e kad se napuni (DropOldest).
/// </summary>
public sealed class BoundedOrderBookQueue : IDisposable
{
    private readonly Channel<OrderBookUpdate> _channel;
    private readonly ILogger _log;
    private readonly CryptoOrderBookRepository? _orderBookRepo;
    private readonly Func<OrderBookUpdate, Task>? _onOrderBookAsync;
    private readonly int _batchSize;
    private readonly TimeSpan _maxBatchDelay;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public BoundedOrderBookQueue(
        int capacity,
        CryptoOrderBookRepository? orderBookRepo,
        Func<OrderBookUpdate, Task>? onOrderBookAsync = null,
        int batchSize = 50,
        TimeSpan? maxBatchDelay = null,
        ILogger? log = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _orderBookRepo = orderBookRepo;
        _onOrderBookAsync = onOrderBookAsync;
        _batchSize = batchSize > 0 ? batchSize : 50;
        _maxBatchDelay = maxBatchDelay ?? TimeSpan.FromMilliseconds(1000);

        var opts = new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        };

        _channel = Channel.CreateBounded<OrderBookUpdate>(opts);
        _log = log ?? Log.ForContext<BoundedOrderBookQueue>();

        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var buffer = new List<OrderBookUpdate>(_batchSize);
        var lastFlushUtc = DateTime.UtcNow;

        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var update))
                {
                    // 1) Custom handler (ako je prosleđen) - npr. za trading logiku
                    if (_onOrderBookAsync is not null)
                    {
                        try
                        {
                            await _onOrderBookAsync(update).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning(ex, "[OB-QUEUE] onOrderBookAsync handler failed for {Ex}:{Sym}", 
                                update.Symbol.ExchangeId, update.Symbol.PublicSymbol);
                        }
                    }

                    // 2) DB batch buffer
                    if (_orderBookRepo is not null)
                    {
                        buffer.Add(update);
                    }

                    var now = DateTime.UtcNow;

                    if (_orderBookRepo is not null &&
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
            _log.Error(ex, "[OB-QUEUE] RunAsync crashed");
        }

        // finalni flush
        if (_orderBookRepo is not null && buffer.Count > 0)
        {
            try
            {
                await FlushBatchAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[OB-QUEUE] Final flush failed");
            }
        }
    }

    private async Task FlushBatchAsync(List<OrderBookUpdate> buffer, CancellationToken ct)
    {
        if (_orderBookRepo is null || buffer.Count == 0)
            return;

        var snapshot = buffer.ToArray();
        buffer.Clear();

        try
        {
            await _orderBookRepo.BatchInsertAsync(snapshot, ct).ConfigureAwait(false);
            _log.Debug("[DB-CRYPTO-OB] Flushed batch count={Count}", snapshot.Length);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[DB-CRYPTO-OB] batch insert failed (count={Count})", snapshot.Length);
        }
    }

    /// <summary>
    /// Non-blocking enqueue; zbog FullMode=DropOldest praktično
    /// vraća false samo ako je queue zatvoren / disposing.
    /// </summary>
    public bool TryEnqueue(OrderBookUpdate update)
    {
        if (_cts.IsCancellationRequested)
            return false;

        return _channel.Writer.TryWrite(update);
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
    }
}

