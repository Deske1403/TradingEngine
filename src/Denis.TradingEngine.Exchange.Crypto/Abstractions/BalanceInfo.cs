using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Stanje balansa za jedan asset na kripto berzi.
/// Ovo koriste risk/cash slojevi da znaju koliko stvarno imamo.
/// </summary>
public sealed record BalanceInfo(
    CryptoExchangeId ExchangeId,
    string Asset,
    decimal Free,
    decimal Locked)
{
    public decimal Total => Free + Locked;
}