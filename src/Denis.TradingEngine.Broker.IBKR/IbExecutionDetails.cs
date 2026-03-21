using System;

namespace Denis.TradingEngine.Broker.IBKR
{
    /// <summary>
    /// Minimalni DTO za fill koji nam treba u order servisu.
    /// Kasnije ga zameni stvarnim IB tipom.
    /// </summary>
    public sealed class IbExecutionDetails : EventArgs
    {
        public int OrderId { get; set; }

        /// <summary>Koliko je stvarno popunjeno u ovom eventu.</summary>
        public decimal FilledQuantity { get; set; }

        /// <summary>Cena po kojoj je popunjeno.</summary>
        public decimal FillPrice { get; set; }

        /// <summary>Da li je ovim eventom order završen (filled/canceled).</summary>
        public bool IsFinal { get; set; }

        public string? ExecId { get; set; }
    }
}