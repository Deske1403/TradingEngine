#nullable enable
using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Config;

/// <summary>
/// Fee struktura za crypto exchange-e.
/// Podržava Maker/Taker fees i razlikovanje između Spot i Futures/Perpetual.
/// </summary>
public sealed class CryptoFeeSchedule
{
    /// <summary>
    /// Exchange ID za koji važi ovaj fee schedule.
    /// </summary>
    public CryptoExchangeId ExchangeId { get; set; }
    
    /// <summary>
    /// Tip trgovine: "Spot", "Futures", "Perpetual", ili null za sve tipove.
    /// </summary>
    public string? TradeType { get; set; }
    
    /// <summary>
    /// Maker fee kao decimal (npr. 0.001 = 0.1%).
    /// </summary>
    public decimal MakerFeePercent { get; set; }
    
    /// <summary>
    /// Taker fee kao decimal (npr. 0.0005 = 0.05%).
    /// </summary>
    public decimal TakerFeePercent { get; set; }
    
    /// <summary>
    /// Izračunava fee u USD na osnovu notional vrednosti i tipa naloga (Maker/Taker).
    /// </summary>
    public decimal CalculateFeeUsd(decimal notionalUsd, bool isMaker)
    {
        var feePercent = isMaker ? MakerFeePercent : TakerFeePercent;
        return notionalUsd * feePercent;
    }
    
    /// <summary>
    /// Izračunava round-trip fee (buy + sell) u USD.
    /// </summary>
    public decimal CalculateRoundTripFeeUsd(decimal notionalUsd, bool buyIsMaker, bool sellIsMaker)
    {
        var buyFee = CalculateFeeUsd(notionalUsd, buyIsMaker);
        var sellFee = CalculateFeeUsd(notionalUsd, sellIsMaker);
        return buyFee + sellFee;
    }
}
