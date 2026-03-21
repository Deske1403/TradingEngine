#nullable enable
using System;
using Denis.TradingEngine.Core.Trading; // zbog Symbol, OrderSide, OrderType, TimeInForce

namespace Denis.TradingEngine.Core.Orders
{
    /// <summary>
    /// Standardni nalog koji šaljemo ka brokeru / simulatoru.
    /// SADA podržava i fractional quantity (decimal).
    /// </summary>
    public sealed class OrderRequest
    {
        public Symbol Symbol { get; }
        public OrderSide Side { get; }
        public OrderType Type { get; }
        public decimal Quantity { get; }          
        public decimal? LimitPrice { get; }
        public TimeInForce Tif { get; }
        public string CorrelationId { get; }
        public DateTime TimestampUtc { get; }
        public string? OcoGroupId { get; }
        /// <summary>Za OCO: stop cena drugog naloga u paru (Bitfinex price_oco_stop). Opciono.</summary>
        public decimal? OcoStopPrice { get; }
        public decimal? StopPrice { get; set; }
        public bool IsExit { get; }

        public OrderRequest(
        Symbol symbol,
        OrderSide side,
        OrderType type,
        decimal quantity,
        decimal? limitPrice,
        TimeInForce tif,
        string correlationId,
        DateTime timestampUtc,
        string? ocoGroupId = null,
        decimal? ocoStopPrice = null,
        decimal? stopPrice = null,
        bool isExit = false)
        {
            Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            Side = side;
            Type = type;
            Quantity = quantity;
            LimitPrice = limitPrice;
            Tif = tif;
            CorrelationId = correlationId;
            TimestampUtc = timestampUtc;

            OcoGroupId = ocoGroupId;
            OcoStopPrice = ocoStopPrice;
            StopPrice = stopPrice;
            IsExit = isExit;
        }
    }
}