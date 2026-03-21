#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingOfferRequest(
    string Symbol,
    decimal Amount,
    decimal Rate,
    int PeriodDays,
    string OfferType,
    int Flags
);
