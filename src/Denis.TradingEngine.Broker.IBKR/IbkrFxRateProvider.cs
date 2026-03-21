#nullable enable
using Denis.TradingEngine.Broker.IBKR.IBKRWrapper;
using IBApi;
using Serilog;

namespace Denis.TradingEngine.Broker.IBKR
{
    public sealed class IbkrFxRateProvider : IDisposable
    {
        private readonly IbkrDefaultWrapper _w;
        private readonly ILogger _log = Log.ForContext<IbkrFxRateProvider>();

        private readonly object _sync = new();
        private int _nextReqId = 12000;
        private int? _activeReqId;
        private TaskCompletionSource<decimal>? _tcs;

        private decimal? _bid;
        private decimal? _ask;
        private decimal? _last;

        public IbkrFxRateProvider(IbkrDefaultWrapper wrapper)
        {
            _w = wrapper ?? throw new ArgumentNullException(nameof(wrapper));
            _w.TickPriceArrived += OnTickPriceArrived;
        }

        public async Task<decimal?> GetFxRateAsync(string fromCcy, string toCcy, TimeSpan timeout, CancellationToken ct)
        {
            if (string.Equals(fromCcy, toCcy, StringComparison.OrdinalIgnoreCase))
                return 1m;

            TaskCompletionSource<decimal> tcs;
            int reqId;

            lock (_sync)
            {
                if (_activeReqId is not null)
                    return null;

                reqId = Interlocked.Increment(ref _nextReqId);
                _activeReqId = reqId;

                _bid = null;
                _ask = null;
                _last = null;

                tcs = new TaskCompletionSource<decimal>(TaskCreationOptions.RunContinuationsAsynchronously);
                _tcs = tcs;
            }

            try
            {
                var client = _w.ClientSocket;
                if (client is null)
                {
                    _log.Warning("[FX] wrapper.ClientSocket is null");
                    return null;
                }

                var c = new Contract
                {
                    SecType = "CASH",
                    Symbol = fromCcy.ToUpperInvariant(),
                    Currency = toCcy.ToUpperInvariant(),
                    Exchange = "IDEALPRO"
                };

                // SNAPSHOT: ne zovemo cancelMktData u finally (IBKR ume da završi snapshot sam)
                client.reqMktData(
                    reqId,
                    c,
                    genericTickList: "",
                    snapshot: true,
                    regulatorySnapshot: false,
                    mktDataOptions: null);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(timeout);

                var rate = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                return rate;
            }
            catch (OperationCanceledException)
            {
                _log.Warning("[FX] timeout {From}/{To}", fromCcy, toCcy);
                return null;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FX] failed {From}/{To}", fromCcy, toCcy);
                return null;
            }
            finally
            {
                lock (_sync)
                {
                    if (_activeReqId == reqId)
                    {
                        _activeReqId = null;
                        _tcs = null;
                    }
                }

                // Namerno prazno:
                // snapshot=true -> bez cancelMktData(reqId), da izbegnemo IBKR error 300 "Can't find EId"
            }
        }

        private void OnTickPriceArrived(int reqId, int tickType, double price)
        {
            TaskCompletionSource<decimal>? tcs = null;
            decimal? mid = null;

            lock (_sync)
            {
                if (_activeReqId != reqId) return;
                if (price <= 0) return;

                var p = (decimal)price;

                if (tickType == 1) _bid = p;       // BID
                else if (tickType == 2) _ask = p;  // ASK
                else if (tickType == 4) _last = p; // LAST

                if (_bid is { } b && _ask is { } a && b > 0m && a > 0m)
                    mid = (b + a) / 2m;
                else if (_last is { } l && l > 0m)
                    mid = l;

                if (mid is { } m && m > 0m)
                {
                    tcs = _tcs;
                    _tcs = null;
                    _activeReqId = null;
                }
            }

            tcs?.TrySetResult(mid!.Value);
        }

        public void Dispose()
        {
            _w.TickPriceArrived -= OnTickPriceArrived;
        }
    }
}