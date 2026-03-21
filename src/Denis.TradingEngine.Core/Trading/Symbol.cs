#nullable enable

namespace Denis.TradingEngine.Core.Trading
{
    /// <summary>
    /// Predstavlja simbol kojim trgujemo na tržištu.
    /// </summary>
    public sealed record Symbol(string Ticker, string Currency = "USD", string Exchange = "SMART");
}