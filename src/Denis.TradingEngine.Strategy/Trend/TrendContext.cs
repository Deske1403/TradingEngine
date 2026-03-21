#nullable enable
using System;

namespace Denis.TradingEngine.Strategy.Trend;

public sealed record TrendContext(
    TrendDirection Direction,
    decimal Score,
    string Source,
    DateTime ComputedAtUtc
)
{
    public LocalTrendQualitySnapshot? LocalQuality { get; init; }
}
