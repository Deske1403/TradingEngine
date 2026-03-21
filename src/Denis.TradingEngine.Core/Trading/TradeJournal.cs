using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Denis.TradingEngine.Core.Trading
{
    /// <summary>
    /// Standardizovan zapis o trejdu koji mogu da koriste i App i Data sloj.
    /// </summary>
    public sealed record TradeJournalEntry(
        DateTime Utc,
        string Symbol,
        string Side,
        decimal Quantity,
        decimal Price,
        decimal Notional,
        decimal RealizedPnl,
        bool IsPaper,
        bool IsExit,
        string? Strategy,
        string? CorrelationId,
        string? BrokerOrderId,
        decimal? EstimatedFeeUsd,
        decimal? PlannedPrice,
        decimal? RiskFraction = null,  // risk fraction used for sizing (e.g. 0.02 = 2%)
        decimal? AtrUsed = null,        // ATR value used for sizing (after floor)
        decimal? PriceRisk = null,       // price risk distance (max of % or ATR-based SL)
        string? Exchange = null         // exchange identifier (IBKR, Kraken, Bitfinex, etc.)
    );
}
