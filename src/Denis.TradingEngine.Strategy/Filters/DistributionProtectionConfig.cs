#nullable enable

namespace Denis.TradingEngine.Strategy.Filters;

/// <summary>
/// Configuration for Distribution Protection filters (anti-manufactured-spike protection).
/// </summary>
public sealed class DistributionProtectionConfig
{
    /// <summary>
    /// Master switch - enables/disables all distribution protection filters.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Whether to log even when disabled (for analysis).
    /// </summary>
    public bool LogWhenDisabled { get; init; } = true;

    /// <summary>
    /// Rejection Speed filter configuration.
    /// Detects rapid price drops after local max (distribution pattern).
    /// </summary>
    public RejectionSpeedConfig RejectionSpeed { get; init; } = new();

    /// <summary>
    /// Time-of-day hard rule configuration.
    /// Detects "open trap" scenarios (fake breakouts early in open_1h phase).
    /// </summary>
    public TimeOfDayConfig TimeOfDay { get; init; } = new();
}

/// <summary>
/// Rejection Speed filter configuration.
/// </summary>
public sealed class RejectionSpeedConfig
{
    /// <summary>
    /// Enable/disable this specific filter.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Minimum price drop percentage to trigger rejection (e.g., 0.0030 = 0.30%).
    /// </summary>
    public decimal DropPctThreshold { get; init; } = 0.0030m; // 0.30%

    /// <summary>
    /// Time window in seconds to measure the drop (e.g., 60 = 60 seconds).
    /// </summary>
    public int WindowSec { get; init; } = 60;
}

/// <summary>
/// Time-of-day hard rule configuration.
/// </summary>
public sealed class TimeOfDayConfig
{
    /// <summary>
    /// Enable/disable this specific filter.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Maximum minutes from RTH open to consider it "early" (e.g., 8 = 8 minutes).
    /// </summary>
    public int MaxMinutesFromOpen { get; init; } = 8;

    /// <summary>
    /// Minimum move percentage from entry to consider it "large" (e.g., 0.007 = 0.7%).
    /// </summary>
    public decimal MinMovePct { get; init; } = 0.007m; // 0.7%

    /// <summary>
    /// Whether to require valid pullback structure (if false, any breakout is rejected if other conditions match).
    /// </summary>
    public bool RequireValidPullback { get; init; } = true;
}
