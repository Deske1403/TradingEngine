#nullable enable
using System;

namespace Denis.TradingEngine.Strategy.Trend;

public sealed class TrendAnalysisSettings
{
    public bool EnableTrendAnalysis { get; init; } = false;
    public int TrendTimeWindowMinutes { get; init; } = 180;
    public bool TrendUseQualityScoring { get; init; } = true;
    public int TrendMinPoints { get; init; } = 30;
    public decimal TrendNeutralThresholdFraction { get; init; } = 0.0003m;
    public decimal TrendEndpointWeight { get; init; } = 0.55m;
    public decimal TrendSlopeWeight { get; init; } = 0.45m;
    public decimal TrendDrawdownPenaltyWeight { get; init; } = 0.15m;
    public decimal TrendMaxDrawdownClampFraction { get; init; } = 0.05m;
    public LocalTrendQualitySettings LocalTrendQuality { get; init; } = new();

    // IBKR optional explicit local-time range mode
    public bool TrendUseExplicitRange { get; init; } = false;
    public int TrendUseExplicitRangeMins { get; init; } = 180;
    // Session range used by explicit trend lookback (exchange-local time).
    // Default = regular US equities session.
    public string TrendRangeStartLocal { get; init; } = "09:30:00";
    public string TrendRangeEndLocal { get; init; } = "16:00:00";
    public string TrendRangeTimeZone { get; init; } = "America/New_York";
    public TimeSpan? RthStartUtc { get; init; }
    public TimeSpan? RthEndUtc { get; init; }

    public TimeSpan GetStartLocalOrDefault(TimeSpan fallback)
    {
        return TimeSpan.TryParse(TrendRangeStartLocal, out var ts) ? ts : fallback;
    }

    public TimeSpan GetEndLocalOrDefault(TimeSpan fallback)
    {
        return TimeSpan.TryParse(TrendRangeEndLocal, out var ts) ? ts : fallback;
    }
}
