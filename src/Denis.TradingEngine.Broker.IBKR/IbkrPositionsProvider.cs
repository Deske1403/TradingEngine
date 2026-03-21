#nullable enable
using Denis.TradingEngine.Broker.IBKR.IBKRWrapper;
using Denis.TradingEngine.Core.Interfaces;
using Serilog;
using System.Globalization;


namespace Denis.TradingEngine.Broker.IBKR
{
    public sealed class IbkrPositionsProvider : IExternalPositionsProvider
    {
        private readonly IbkrDefaultWrapper _wrapper;
        private readonly ILogger _log = Log.ForContext<IbkrPositionsProvider>();

        private readonly object _sync = new();
        private Task<IReadOnlyList<ExternalPositionDto>>? _inFlight;

        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

        public IbkrPositionsProvider(IbkrDefaultWrapper wrapper)
        {
            _wrapper = wrapper ?? throw new ArgumentNullException(nameof(wrapper));
        }

        public Task<IReadOnlyList<ExternalPositionDto>> GetOpenPositionsAsync(CancellationToken ct = default)
        {
            lock (_sync)
            {
                if (_inFlight is not null)
                    return _inFlight;

                _inFlight = GetOpenPositionsInternalAsync(ct);
                return _inFlight;
            }
        }

        private async Task<IReadOnlyList<ExternalPositionDto>> GetOpenPositionsInternalAsync(CancellationToken ct)
        {
            var list = new List<ExternalPositionDto>();
            var tcs = new TaskCompletionSource<IReadOnlyList<ExternalPositionDto>>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Cleanup()
            {
                _wrapper.PositionReceived -= OnPos;
                _wrapper.PositionsEnd -= OnEnd;

                try { _wrapper.ClientSocket?.cancelPositions(); } catch { }
            }

            void OnPos(string account, string symbol, decimal qty, decimal avg)
            {
                var sym = string.IsNullOrWhiteSpace(symbol) ? "<n/a>" : symbol.Trim();

                list.Add(new ExternalPositionDto(sym, qty, avg));

                _log.Debug(
                    "[IB-POS] {Acct} {Sym} qty={Qty} avg={Avg}",
                    account,
                    sym,
                    qty.ToString("F4", CultureInfo.InvariantCulture),
                    avg.ToString("F6", CultureInfo.InvariantCulture)
                );
            }

            void OnEnd()
            {
                Cleanup();

                // De-dupe po simbolu (ako dobiješ duple evente iz bilo kog razloga)
                // Zadrži poslednji (najnoviji) entry po simbolu.
                var distinct = list
                    .GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last())
                    .ToList()
                    .AsReadOnly();

                tcs.TrySetResult(distinct);
            }

            _wrapper.PositionReceived += OnPos;
            _wrapper.PositionsEnd += OnEnd;

            if (_wrapper.ClientSocket is null)
            {
                Cleanup();
                tcs.TrySetResult(Array.Empty<ExternalPositionDto>());
                return await tcs.Task.ConfigureAwait(false);
            }

            using var timeoutCts = new CancellationTokenSource(DefaultTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var reg = linked.Token.Register(() =>
            {
                Cleanup();
                tcs.TrySetCanceled(linked.Token);
            });

            try
            {
                _wrapper.ClientSocket.reqPositions();

                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Warning("[IB-POS] positions request canceled/timeout");
                return Array.Empty<ExternalPositionDto>();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[IB-POS] positions request failed");
                return Array.Empty<ExternalPositionDto>();
            }
            finally
            {
                lock (_sync)
                {
                    _inFlight = null;
                }
            }
        }
    }
}