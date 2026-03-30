#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingDecision(
    string Action,
    bool IsDryRun,
    bool IsActionable,
    string Symbol,
    string Currency,
    string WalletType,
    decimal AvailableBalance,
    decimal LendableBalance,
    decimal? ProposedAmount,
    decimal? ProposedRate,
    int? ProposedPeriodDays,
    string Reason,
    DateTime TimestampUtc,
    string? TargetOfferId = null,
    string? SlotRole = null,
    int? SlotIndex = null,
    int? SlotCount = null
);
