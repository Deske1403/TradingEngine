#nullable enable
using System;
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.Core.Interfaces
{
    /// <summary>
    /// Strategija koja prima tok cena i proizvodi signale za ulaz.
    /// </summary>
    public interface ITradingStrategy
    {
        /// <summary>
        /// Strategiji dostavljamo novi citat sa tržišta.
        /// </summary>
        void OnQuote(MarketQuote quote);

        /// <summary>
        /// Kada strategija prepozna priliku, emituje signal.
        /// </summary>
        event Action<TradeSignal>? TradeSignalGenerated;
    }
}