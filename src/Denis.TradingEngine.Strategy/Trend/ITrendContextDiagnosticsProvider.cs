#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Denis.TradingEngine.Strategy.Trend;

public interface ITrendContextDiagnosticsProvider
{
    Task<TrendContextDiagnosticsResult> GetTrendContextDiagnosticsAsync(
        string exchange,
        string symbol,
        DateTime quoteTsUtc,
        CancellationToken ct = default);
}
