#nullable enable
using System;
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.Orders
{
    /// <summary>
    /// Standardizovan zapis o "pending" nalogu (rezervisan keš, vreme, originalni zahtev).
    /// Isti model će koristiti i orkestrator i budući OrderCoordinator.
    /// </summary>
    public sealed record PendingOrder(
        OrderRequest Req,
        decimal ReservedUsd,
        DateTime AtUtc,
        string? BrokerOrderId = null,   // već si imao ovo
        decimal LastFeeUsd = 0m,        // NOVO: poslednja provizija
        string? LastExecId = null       // NOVO: na koji exec se fee odnosi
    )
    {
        public string CorrelationId => Req.CorrelationId;
        public Symbol Symbol => Req.Symbol;
        public bool IsBuy => Req.Side == OrderSide.Buy;
        public bool IsSell => Req.Side == OrderSide.Sell;
    }
}