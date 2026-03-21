#nullable enable
using System;
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.Core.Positions
{
    /// <summary>
    /// Jednostavan in-memory tracker izloženosti (USD) po simbolu.
    /// Održava i ukupan "baseline" kapital radi računanja frakcije izloženosti.
    /// </summary>
    public interface IExposureTracker
    {
        decimal TotalBaselineCapitalUsd { get; }

        /// <summary> Trenutna izloženost za simbol (u USD). </summary>
        decimal GetExposureUsd(Symbol symbol);

        /// <summary> Trenutna izloženost kao frakcija ukupnog kapitala. </summary>
        decimal GetExposureFraction(Symbol symbol);

        /// <summary>
        /// Provera da li smemo da povećamo izloženost za traženi iznos, s obzirom na limit (frakcija).
        /// </summary>
        bool CanAllocate(Symbol symbol, decimal addUsd, decimal maxPerSymbolFraction, out string? reason);

        /// <summary>
        /// Rezerviši izloženost (npr. nakon place naloga).
        /// </summary>
        void Reserve(Symbol symbol, decimal usd);

        /// <summary>
        /// Oslobodi izloženost (npr. posle prodaje / cancel/replace).
        /// </summary>
        void Release(Symbol symbol, decimal usd);
    }
}
