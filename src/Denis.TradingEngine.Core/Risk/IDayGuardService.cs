#nullable enable
using System;
using System.Collections.Generic;
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.Core.Risk
{
    /// <summary>
    /// Dnevne kočnice: ograničavaju trgovanje ako se ispune "loši" uslovi u toku dana.
    /// - Max broj naloga po simbolu / globalno
    /// - Dnevni gubitak (PnL) preko praga
    /// </summary>
    public interface IDayGuardService
    {
        /// <summary>Reset dnevnog stanja ako je počeo novi UTC dan.</summary>
        void ResetIfNewDay(DateTime utcNow);

        /// <summary>Provera da li je dozvoljeno poslati novi nalog za simbol.</summary>
        bool CanTrade(Symbol symbol, DateTime utcNow, out string? reason);

        /// <summary>Obeleži da je poslat nalog (paper ili real), da brojači budu tačni.</summary>
        void OnOrderPlaced(Symbol symbol, DateTime utcNow);

        /// <summary>
        /// Vrati trade count ako entry nalog nikad nije dobio fill i završio je kao canceled/rejected/expired.
        /// </summary>
        void OnEntryOrderVoided(Symbol symbol, DateTime utcNow);

        /// <summary>Javi realizovani PnL (USD) tokom dana. Negativne vrednosti pomeraju dnevni gubitak.</summary>
        void OnRealizedPnl(decimal realizedPnlUsd, DateTime utcNow);

        /// <summary>Trenutni zbir po simbolu (samo za debug/telemetriju).</summary>
        IReadOnlyDictionary<string, int> CurrentTradeCountPerSymbol { get; }

        /// <summary>Trenutni dnevni broj naloga (ukupno).</summary>
        int CurrentTradeCountTotal { get; }

        /// <summary>Trenutni dnevni realizovani PnL (USD).</summary>
        decimal CurrentRealizedPnlUsd { get; }

        /// <summary>Aktivna zabrana za današnji dan (npr. zbog dnevnog gubitka).</summary>
        bool IsDayLocked { get; }

        /// <summary>
        /// Restorira stanje iz baze (koristi se pri restart-u aplikacije).
        /// </summary>
        void RestoreState(
            IReadOnlyDictionary<string, int> tradesPerSymbol,
            int tradesTotal,
            decimal realizedPnlUsd,
            DateTime utcNow);
    }
}
