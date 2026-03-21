#nullable enable
using System;
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.Core.Interfaces
{
    /// <summary>
    /// Izvor tržišnih podataka — pretplata na kretanja cena.
    /// Ovo implementira IBKR adapter (a kasnije i kripto adapter).
    /// </summary>
    public interface IMarketDataFeed
    {
        /// <summary>
        /// Pretplata na citate (najbolja ponuda / tražnja / last)
        /// </summary>
        void SubscribeQuotes(Symbol symbol);

        /// <summary>
        /// Odjava sa tržišnih podataka za simbol (opciono).
        /// </summary>
        void UnsubscribeQuotes(Symbol symbol);

        /// <summary>
        /// Događaj — svaki put kada stigne nova cena.
        /// Timestamp mora biti UTC.
        /// </summary>
        event Action<MarketQuote>? MarketQuoteUpdated;
    }
}