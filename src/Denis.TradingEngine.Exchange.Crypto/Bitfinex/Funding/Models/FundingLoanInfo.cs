#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingLoanInfo(
    long LoanId,
    string Symbol,
    string? Side,
    string Status,
    decimal Amount,
    decimal? OriginalAmount,
    decimal? Rate,
    int? PeriodDays,
    DateTime? CreatedUtc,
    DateTime? UpdatedUtc,
    DateTime? OpenedUtc,
    DateTime? LastPayoutUtc,
    string? FundingType,
    decimal? RateReal,
    bool Notify,
    bool Renew,
    bool NoClose,
    string? PositionPair,
    object? Metadata = null);
