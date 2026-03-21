#nullable enable
using Denis.TradingEngine.Core.Crypto;
using Microsoft.Extensions.Configuration;

namespace Denis.TradingEngine.Exchange.Crypto.Config;

/// <summary>
/// Provider za crypto fee schedule-e. Učitava fees iz config-a i vraća odgovarajući schedule.
/// </summary>
public static class CryptoFeeProvider
{
    /// <summary>
    /// Učitava fee schedule za exchange iz config-a.
    /// Ako nije pronađen u config-u, vraća default vrednosti na osnovu poznatih fee struktura.
    /// </summary>
    public static CryptoFeeSchedule GetFeeSchedule(CryptoExchangeId exchangeId, IConfiguration? config = null, string? tradeType = null)
    {
        // Prvo probaj da učitaš iz config-a
        if (config != null)
        {
            var feeSection = config.GetSection($"Fees:{exchangeId}");
            if (feeSection.Exists())
            {
                var makerFee = feeSection.GetValue<decimal>("MakerFeePercent", 0m);
                var takerFee = feeSection.GetValue<decimal>("TakerFeePercent", 0m);
                var configTradeType = feeSection.GetValue<string>("TradeType");
                
                // Ako je tradeType specificiran, proveri da li se poklapa
                if (string.IsNullOrWhiteSpace(tradeType) || string.IsNullOrWhiteSpace(configTradeType) || 
                    string.Equals(tradeType, configTradeType, StringComparison.OrdinalIgnoreCase))
                {
                    if (makerFee > 0m || takerFee > 0m)
                    {
                        return new CryptoFeeSchedule
                        {
                            ExchangeId = exchangeId,
                            TradeType = configTradeType,
                            MakerFeePercent = makerFee,
                            TakerFeePercent = takerFee
                        };
                    }
                }
            }
        }
        
        // Fallback na poznate default vrednosti
        return GetDefaultFeeSchedule(exchangeId, tradeType);
    }
    
    /// <summary>
    /// Vraća default fee schedule na osnovu poznatih fee struktura.
    /// </summary>
    private static CryptoFeeSchedule GetDefaultFeeSchedule(CryptoExchangeId exchangeId, string? tradeType = null)
    {
        return exchangeId switch
        {
            CryptoExchangeId.Deribit => new CryptoFeeSchedule
            {
                ExchangeId = exchangeId,
                TradeType = "Futures",
                MakerFeePercent = 0.0000m,  // 0.00%
                TakerFeePercent = 0.0005m   // 0.05%
            },
            
            CryptoExchangeId.Bitfinex => new CryptoFeeSchedule
            {
                ExchangeId = exchangeId,
                TradeType = "Spot",
                MakerFeePercent = 0.0000m,  // 0.00% (Zero Fee promocija)
                TakerFeePercent = 0.0000m   // 0.00%
            },
            
            CryptoExchangeId.Bybit => tradeType?.Equals("Futures", StringComparison.OrdinalIgnoreCase) == true ||
                                      tradeType?.Equals("Perpetual", StringComparison.OrdinalIgnoreCase) == true
                ? new CryptoFeeSchedule
                {
                    ExchangeId = exchangeId,
                    TradeType = "Futures",
                    MakerFeePercent = 0.0002m,  // 0.02%
                    TakerFeePercent = 0.00055m   // 0.055%
                }
                : new CryptoFeeSchedule
                {
                    ExchangeId = exchangeId,
                    TradeType = "Spot",
                    MakerFeePercent = 0.001m,   // 0.10%
                    TakerFeePercent = 0.001m    // 0.10%
                },
            
            CryptoExchangeId.Kraken => tradeType?.Equals("Futures", StringComparison.OrdinalIgnoreCase) == true
                ? new CryptoFeeSchedule
                {
                    ExchangeId = exchangeId,
                    TradeType = "Futures",
                    MakerFeePercent = 0.0002m,  // 0.02%
                    TakerFeePercent = 0.0005m    // 0.05%
                }
                : new CryptoFeeSchedule
                {
                    ExchangeId = exchangeId,
                    TradeType = "Spot",
                    MakerFeePercent = 0.0025m,  // 0.25% (Kraken Pro)
                    TakerFeePercent = 0.004m    // 0.40%
                },
            
            _ => new CryptoFeeSchedule
            {
                ExchangeId = exchangeId,
                TradeType = tradeType ?? "Spot",
                MakerFeePercent = 0.001m,   // Default 0.1%
                TakerFeePercent = 0.001m    // Default 0.1%
            }
        };
    }
}
