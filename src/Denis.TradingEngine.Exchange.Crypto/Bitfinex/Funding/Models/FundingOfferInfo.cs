#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingOfferInfo(
    string OfferId,
    string Symbol,
    DateTime? CreatedUtc,
    DateTime? UpdatedUtc,
    decimal Amount,
    decimal OriginalAmount,
    string OfferType,
    int Flags,
    string Status,
    decimal Rate,
    int PeriodDays,
    bool Notify,
    bool Hidden,
    bool Renew,
    decimal? RateReal)
{
    public bool IsActive =>
        Status.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
        Status.Contains("PARTIALLY", StringComparison.OrdinalIgnoreCase);
}
