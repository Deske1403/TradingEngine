#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingTickerSnapshot(
    string Symbol,
    decimal? Frr,
    decimal BidRate,
    int BidPeriodDays,
    decimal BidSize,
    decimal AskRate,
    int AskPeriodDays,
    decimal AskSize,
    DateTime TimestampUtc
);
