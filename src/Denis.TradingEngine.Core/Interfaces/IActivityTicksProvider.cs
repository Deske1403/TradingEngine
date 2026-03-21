#nullable enable
namespace Denis.TradingEngine.Core.Interfaces;

/// <summary>
/// Izvor broja "activity ticks" u prozoru (za pullback / SignalSlayer).
/// Implementacija može biti in-memory (ažurira se na TradeReceived) – bez poziva baze za odluku.
/// Koriste i IBKR i crypto; host injektuje odgovarajuću implementaciju ili null (fallback na brojanje OnQuote).
/// </summary>
public interface IActivityTicksProvider
{
    /// <summary>
    /// Broj tikova (trade-ova) za dati exchange/symbol od sinceUtc do sada.
    /// </summary>
    int GetTicksInWindow(string exchange, string symbol, DateTime sinceUtc);
}
