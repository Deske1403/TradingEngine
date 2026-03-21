#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingWalletBalance(
    string WalletType,
    string Currency,
    decimal Total,
    decimal Available,
    decimal Reserved
);
