#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingTradeInfo(
    long FundingTradeId,
    string Symbol,
    DateTime Utc,
    long? OfferId,
    decimal Amount,
    decimal? Rate,
    int? PeriodDays,
    bool? Maker,
    object? Metadata = null);
