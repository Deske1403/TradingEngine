#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingShadowAction(
    string Symbol,
    string Currency,
    string Regime,
    string Bucket,
    string Action,
    bool IsActionable,
    decimal AvailableBalance,
    decimal LendableBalance,
    decimal AllocationAmount,
    decimal AllocationFraction,
    decimal? TargetRate,
    decimal? FallbackRate,
    int? TargetPeriodDays,
    int? MaxWaitMinutes,
    DateTime? DecisionDeadlineUtc,
    string? Role,
    string? FallbackBucket,
    int ActiveOfferCount,
    long? ActiveOfferId,
    decimal? ActiveOfferRate,
    decimal? ActiveOfferAmount,
    string? ActiveOfferStatus,
    string Reason,
    string Summary,
    DateTime TimestampUtc);
