#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding;

internal static class BitfinexFundingSymbolNormalizer
{
    public static string Normalize(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return string.Empty;

        var trimmed = symbol.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        if (trimmed.Length == 1)
            return char.ToLowerInvariant(trimmed[0]).ToString();

        var prefix = char.ToLowerInvariant(trimmed[0]);
        var suffix = trimmed.Substring(1).ToUpperInvariant();
        return prefix + suffix;
    }
}
