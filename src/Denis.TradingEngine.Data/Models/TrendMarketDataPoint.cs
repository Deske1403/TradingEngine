#nullable enable
using System;

namespace Denis.TradingEngine.Data.Models
{
    public enum TrendMarketDataSource
    {
        IbkrMarketTick = 1,
        CryptoTrade = 2
    }

    /// <summary>
    /// Zajednicki model za trend analizu koji pokriva:
    /// - market_ticks (IBKR)
    /// - crypto_trades (Crypto)
    /// </summary>
    public sealed class TrendMarketDataPoint
    {
        public TrendMarketDataSource Source { get; init; }

        public DateTime Utc { get; init; }
        public string Exchange { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;

        // market_ticks kolone
        public decimal? Bid { get; init; }
        public decimal? Ask { get; init; }
        public decimal? BidSize { get; init; }
        public decimal? AskSize { get; init; }

        // crypto_trades kolone
        public decimal? Price { get; init; }
        public decimal? Quantity { get; init; }
        public string? Side { get; init; } // buy/sell
        public long? TradeId { get; init; }
    }
}
