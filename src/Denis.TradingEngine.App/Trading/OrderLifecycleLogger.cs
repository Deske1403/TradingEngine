#nullable enable
using System;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Logging;
using Serilog;

namespace Denis.TradingEngine.App.Trading
{
    public sealed class OrderLifecycleLogger : IDisposable
    {
        private readonly ILogger _log = AppLog.ForContext<OrderLifecycleLogger>();
        private readonly IOrderService _service;

        public OrderLifecycleLogger(IOrderService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _service.OrderUpdated += OnOrderUpdated;
        }

        private void OnOrderUpdated(OrderResult r)
        {
            var id = r.BrokerOrderId ?? "?";
            var status = r.Status ?? "Unknown";

            switch (status)
            {
                case "Filled":
                case "PartiallyFilled":
                    _log.Information("[ORD-LC] {Id} {Status} filled={Qty} avg={Avg} fee={Fee}", id, status, r.FilledQuantity, r.AverageFillPrice, r.CommissionAndFees );
                    break;
                case "Commission":
                    _log.Information("[ORD-LC] {Id} COMMISSION {Fee} {Msg}", id, r.CommissionAndFees, r.Message );
                    break;
                case "Canceled":
                case "CancelFailed":
                    _log.Warning("[ORD-LC] {Id} {Status} msg={Msg}", id, status, r.Message
                    );
                    break;
                default:
                    _log.Information("[ORD-LC] {Id} status={Status} filled={Qty} avg={Avg} fee={Fee} msg={Msg}", id, status, r.FilledQuantity, r.AverageFillPrice, r.CommissionAndFees, r.Message);
                    break;
            }
        }

        public void Dispose()
        {
            _service.OrderUpdated -= OnOrderUpdated;
        }
    }
}