#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingShadowPlan(
    string Symbol,
    string Currency,
    string Regime,
    decimal AvailableBalance,
    decimal LendableBalance,
    decimal MarketAskRate,
    decimal MarketBidRate,
    IReadOnlyList<FundingShadowBucket> Buckets,
    string Summary,
    DateTime TimestampUtc);

public sealed record FundingShadowBucket(
    string Bucket,
    decimal AllocationAmount,
    decimal AllocationFraction,
    decimal TargetRate,
    int TargetPeriodDays,
    int MaxWaitMinutes,
    string Role,
    string? FallbackBucket = null);
