#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Denis.TradingEngine.Strategy.Trend;

public interface ITrendContextProvider
{
    Task<TrendContext?> GetTrendContextAsync(
        string exchange,
        string symbol,
        DateTime quoteTsUtc,
        CancellationToken ct = default);
}
