#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

public sealed record FundingLedgerEntry(
    long LedgerId,
    string Currency,
    string WalletType,
    DateTime Utc,
    decimal Amount,
    decimal? BalanceAfter,
    string EntryType,
    string? Description,
    object? Metadata = null);
