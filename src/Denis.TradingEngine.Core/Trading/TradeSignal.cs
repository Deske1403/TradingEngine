#nullable enable
using System;

namespace Denis.TradingEngine.Core.Trading
{
    /// <summary>
    /// Signal strategije da treba otvoriti poziciju.
    /// </summary>
    public sealed record TradeSignal(
        Symbol Symbol,
        bool ShouldEnter,
        OrderSide Side,
        decimal? SuggestedLimitPrice,
        string Reason,
        DateTime TimestampUtc,
        decimal? SpreadBps = null,
        int? ActivityTicks = null,
        string? Regime = null,
        decimal? Slope5 = null,
        decimal? Slope20 = null);
}
