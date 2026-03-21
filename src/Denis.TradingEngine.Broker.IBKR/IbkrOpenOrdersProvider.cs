#nullable enable
using Denis.TradingEngine.Broker.IBKR.IBKRWrapper;
using IBApi;
using Serilog;


namespace Denis.TradingEngine.Broker.IBKR
{
    public sealed record IbOpenOrderInfo(
        int TwsOrderId,
        string? OrderRef,
        string Symbol,
        string Action,
        double TotalQuantity,
        string OrderType,
        double? LimitPrice,
        string Status);

    public sealed class IbkrOpenOrdersProvider
    {
        private readonly IbkrDefaultWrapper _wrapper;
        private readonly ILogger _log = Log.ForContext<IbkrOpenOrdersProvider>();

        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

        public IbkrOpenOrdersProvider(IbkrDefaultWrapper wrapper)
        {
            _wrapper = wrapper ?? throw new ArgumentNullException(nameof(wrapper));
        }

        public Task<IReadOnlyList<IbOpenOrderInfo>> GetOpenOrdersAsync(CancellationToken ct = default)
        {
            return GetOpenOrdersInternalAsync(ct);
        }

        private async Task<IReadOnlyList<IbOpenOrderInfo>> GetOpenOrdersInternalAsync(CancellationToken ct)
        {
            var list = new List<IbOpenOrderInfo>();
            var tcs = new TaskCompletionSource<IReadOnlyList<IbOpenOrderInfo>>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnOpenOrder(int orderId, Contract contract, Order order, OrderState? state)
            {
                try
                {
                    var info = new IbOpenOrderInfo(
                        TwsOrderId: orderId,
                        OrderRef: string.IsNullOrWhiteSpace(order.OrderRef) ? null : order.OrderRef.Trim(),
                        Symbol: contract.Symbol ?? string.Empty,
                        Action: order.Action ?? string.Empty,
                        TotalQuantity:(double)order.TotalQuantity,
                        OrderType: order.OrderType ?? string.Empty,
                        LimitPrice: order.LmtPrice > 0 ? order.LmtPrice : (double?)null,
                        Status: state?.Status ?? "Submitted");

                    list.Add(info);

                    _log.Debug(
                        "[IB-OPEN] {Id} {Sym} {Action} {Qty} {Type} {Px} ref={Ref} status={Status}",
                        orderId,
                        info.Symbol,
                        info.Action,
                        info.TotalQuantity,
                        info.OrderType,
                        info.LimitPrice,
                        info.OrderRef,
                        info.Status);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[IB-OPEN] error capturing openOrder");
                }
            }

            void OnEnd()
            {
                _wrapper.OpenOrderArrived -= OnOpenOrder;
                _wrapper.OpenOrdersEnd -= OnEnd;
                tcs.TrySetResult(list.AsReadOnly());
            }

            _wrapper.OpenOrderArrived += OnOpenOrder;
            _wrapper.OpenOrdersEnd += OnEnd;

            try
            {
                _log.Information("[IB-OPEN] requesting open orders");
                _wrapper.ClientSocket.reqOpenOrders();
            }
            catch (Exception ex)
            {
                _wrapper.OpenOrderArrived -= OnOpenOrder;
                _wrapper.OpenOrdersEnd -= OnEnd;

                _log.Warning(ex, "[IB-OPEN] reqOpenOrders failed immediately");
                return Array.Empty<IbOpenOrderInfo>();
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DefaultTimeout);

            await using var _ = cts.Token.Register(() =>
            {
                _wrapper.OpenOrderArrived -= OnOpenOrder;
                _wrapper.OpenOrdersEnd -= OnEnd;

                if (!tcs.Task.IsCompleted)
                {
                    _log.Warning("[IB-OPEN] timeout while waiting for open orders");
                    tcs.TrySetResult(list.AsReadOnly());
                }
            });

            return await tcs.Task.ConfigureAwait(false);
        }
    }
}
