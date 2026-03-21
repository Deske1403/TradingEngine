#nullable enable
namespace Denis.TradingEngine.Core.Risk
{
    /// <summary>
    /// Podešavanja dnevnih zaštita (Day Guards).
    /// </summary>
    public sealed record DayGuardLimits(
        int MaxTradesPerSymbol,   // npr. 50
        int MaxTradesTotal,       // npr. 200
        decimal DailyLossStopUsd  // npr. 50.00m => ako izgubimo 50$ u danu, dnevni lock
    );
}
