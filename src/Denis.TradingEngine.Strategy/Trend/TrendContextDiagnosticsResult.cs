#nullable enable

namespace Denis.TradingEngine.Strategy.Trend;

public sealed record TrendContextDiagnosticsResult(
    TrendContext? Context,
    string? NoContextReason,
    int RawPointCount,
    int UsablePointCount,
    int RequiredMinPoints);
