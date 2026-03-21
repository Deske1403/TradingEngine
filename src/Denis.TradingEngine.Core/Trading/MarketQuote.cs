#nullable enable
using System;

namespace Denis.TradingEngine.Core.Trading
{
    /// <summary>
    /// Trenutni citat sa tržišta (bid/ask/last).
    /// Timestamp uvek u UTC.
    /// </summary>
    public sealed record MarketQuote(
        Symbol Symbol,
        decimal? Bid,
        decimal? Ask,
        decimal? Last,
        decimal? BidSize,
        decimal? AskSize,
        DateTime TimestampUtc)
    {
        public decimal? Mid => (Bid.HasValue && Ask.HasValue) ? (Bid.Value + Ask.Value) / 2m : null;
        public decimal? Spread => (Bid.HasValue && Ask.HasValue) ? (Ask.Value - Bid.Value) : null;
    }
}
