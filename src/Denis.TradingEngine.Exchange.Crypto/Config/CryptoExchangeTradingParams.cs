using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Config;

/// <summary>
/// Parametri za exit i trejding po berzi (TP/SL, trailing, max hold).
/// Svaka menjacnica može imati svoje vrednosti – npr. Bitfinex (0% fee) može manji TP/SL, više tradeova.
/// </summary>
public sealed class CryptoExchangeTradingParams
{
    /// <summary>Take-profit frakcija od entry cene (npr. 0.02 = 2%).</summary>
    public decimal TpFraction { get; set; } = 0.014m;

    /// <summary>Stop-loss frakcija od entry cene (npr. 0.01 = 1%).</summary>
    public decimal SlFraction { get; set; } = 0.01m;

    /// <summary>Trailing stop: aktivira se kada profit od entry pređe ovu frakciju (npr. 0.01 = 1%).</summary>
    public decimal TrailActivateFraction { get; set; } = 0.01m;

    /// <summary>Trailing stop: stop nivo je ovoliko ispod najbolje cene (npr. 0.005 = 0.5%).</summary>
    public decimal TrailDistanceFraction { get; set; } = 0.005m;

    /// <summary>Maksimalno vreme držanja pozicije u minutima. 0 = koristi swing config.</summary>
    public int MaxHoldTimeMinutes { get; set; }

    /// <summary>
    /// Default parametri po berzi. Svaka menjacnica ima svoje vrednosti prema potrebama (fee, likvidnost, itd.).
    /// Override u configu: u Exchanges[] za svaki exchange stavi "TradingParams": { "TpFraction": ..., ... }.
    /// </summary>
    public static CryptoExchangeTradingParams GetDefault(CryptoExchangeId exchangeId)
    {
        return exchangeId switch
        {
            CryptoExchangeId.Bitfinex => new CryptoExchangeTradingParams
            {
                TpFraction = 0.010m,   // 1.0% TP (manji da češće pogodi pre stopa)
                SlFraction = 0.008m,
                TrailActivateFraction = 0.008m,
                TrailDistanceFraction = 0.004m,
                MaxHoldTimeMinutes = 0
            },
            CryptoExchangeId.Kraken => new CryptoExchangeTradingParams
            {
                TpFraction = 0.025m,  // veći fee – TP malo viši da pokrije
                SlFraction = 0.01m,
                TrailActivateFraction = 0.01m,
                TrailDistanceFraction = 0.005m,
                MaxHoldTimeMinutes = 0
            },
            CryptoExchangeId.Bybit => new CryptoExchangeTradingParams
            {
                TpFraction = 0.02m,
                SlFraction = 0.01m,
                TrailActivateFraction = 0.01m,
                TrailDistanceFraction = 0.005m,
                MaxHoldTimeMinutes = 0
            },
            CryptoExchangeId.Deribit => new CryptoExchangeTradingParams
            {
                TpFraction = 0.02m,
                SlFraction = 0.01m,
                TrailActivateFraction = 0.01m,
                TrailDistanceFraction = 0.005m,
                MaxHoldTimeMinutes = 0
            },
            _ => new CryptoExchangeTradingParams()
        };
    }
}
