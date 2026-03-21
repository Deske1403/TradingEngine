#nullable enable

namespace Denis.TradingEngine.Core.Risk
{
    /// <summary>
    /// Stanje novca po slotovima:
    /// - Free: trenutno poravnato i raspoloživo za nove pozicije
    /// - Settling: u poravnanju (T+1), biće slobodno sutra
    /// - InPositions: trenutno vezano u otvorenim pozicijama
    /// </summary>
    public sealed record CashState(
        decimal Free,
        decimal Settling,
        decimal InPositions
    )
    {
        public decimal Total => Free + Settling + InPositions;
    }
}