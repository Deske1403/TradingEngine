#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingOfferActionResult(
    string Action,
    bool Success,
    bool IsDryRun,
    string Symbol,
    string? OfferId,
    string Status,
    string Message,
    FundingOfferInfo? Offer,
    DateTime TimestampUtc
);
