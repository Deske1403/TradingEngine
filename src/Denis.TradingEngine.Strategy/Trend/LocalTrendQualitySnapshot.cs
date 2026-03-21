#nullable enable
using System;

namespace Denis.TradingEngine.Strategy.Trend;

public sealed record LocalTrendQualitySnapshot(
    bool Enabled,
    bool DryRun,
    string Source,
    DateTime ComputedAtUtc,
    int MagnitudeWindowMinutes,
    int ChopWindowMinutes,
    int MagnitudePoints,
    int ChopPoints,
    decimal MagnitudeNetMoveFraction,
    decimal ChopNetMoveFraction,
    decimal ChopRangeFraction,
    decimal ChopPathFraction,
    decimal ChopEfficiency,
    decimal? ChopToMagnitudeRatio,
    bool WouldPass,
    string DecisionReason
);
