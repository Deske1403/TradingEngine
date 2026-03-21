#nullable enable

namespace Denis.TradingEngine.Strategy.Trend;

public sealed class LocalTrendQualitySettings
{
    public bool Enabled { get; init; } = false;
    public bool DryRun { get; init; } = true;

    // Magnitude/context window (e.g. 30m)
    public int MagnitudeWindowMinutes { get; init; } = 30;
    public int MagnitudeMinPoints { get; init; } = 20;

    // Chop/stability window (e.g. 15m)
    public int ChopWindowMinutes { get; init; } = 15;
    public int ChopMinPoints { get; init; } = 20;

    // Candidate thresholds (used for dry-run "would block" simulation)
    public bool RequirePositiveMagnitude { get; init; } = true;
    public decimal MinMagnitudeFraction { get; init; } = 0m;
    public decimal MaxChopToMagnitudeRatio { get; init; } = 0m; // 0 => disabled
    public decimal MinChopEfficiency { get; init; } = 0m;       // 0 => disabled
}
