using System;

namespace Denis.TradingEngine.Core.Swing
{
    /// <summary>
    /// Jednostavan snapshot swing pozicije.
    /// Kasnije ćemo je puniti iz DB/IBKR i koristiti za swing odluke.
    /// </summary>
    public enum SwingExitPolicy
    {
        None = 0,
        /// <summary>
        /// Izlazimo samo na TP/SL (klasičan OCO).
        /// </summary>
        PriceOnly = 1,
        /// <summary>
        /// Izlazimo ili na TP/SL ili po isteku horizonta (npr. N dana).
        /// </summary>
        PriceOrTime = 2,
        /// <summary>
        /// Ne automatizujemo izlaz, samo pratimo (manual close).
        /// </summary>
        ManualOnly = 3
    }

    public sealed class SwingPositionSnapshot
    {
        public string Symbol { get; init; } = string.Empty;

        /// <summary>
        /// Pozitivno = long, negativno = short.
        /// </summary>
        public decimal Quantity { get; init; }

        public decimal EntryPrice { get; init; }

        /// <summary>
        /// Kada je swing otvoren (po entry fill-u).
        /// </summary>
        public DateTime OpenedUtc { get; init; }

        /// <summary>
        /// Koja strategija je generisala ulaz (npr. "PullbackInUptrendStrategy").
        /// </summary>
        public string Strategy { get; init; } = string.Empty;

        /// <summary>
        /// CorrelationId signala / order flow-a koji je kreirao swing.
        /// </summary>
        public string CorrelationId { get; init; } = string.Empty;

        /// <summary>
        /// Planirani horizont držanja u danima (npr. 2, 3, 5).
        /// 0 = nije definisano (ili koristimo default).
        /// </summary>
        public int PlannedHoldingDays { get; init; }

        public SwingExitPolicy ExitPolicy { get; init; } = SwingExitPolicy.PriceOrTime;

        public bool IsLong  => Quantity > 0;
        public bool IsShort => Quantity < 0;
    }
}