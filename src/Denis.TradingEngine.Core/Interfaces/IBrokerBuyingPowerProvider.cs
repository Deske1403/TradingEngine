namespace Denis.TradingEngine.Core.Interfaces
{
    /// <summary>
    /// Izvor realnog buying power-a sa brokera (IBKR).
    /// Može da vraća "last known" vrednost iz TWS-a.
    /// </summary>
    public interface IBrokerBuyingPowerProvider
    {
        /// <summary>
        /// Zadnja poznata buying-power vrednost u USD.
        /// Nema async baš namerno, ovo se obično drži u memoriji i osvežava događajima.
        /// </summary>
        decimal GetLastKnownBuyingPowerUsd();
    }
}