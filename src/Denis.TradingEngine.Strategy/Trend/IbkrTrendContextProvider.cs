#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Data.Repositories;

namespace Denis.TradingEngine.Strategy.Trend;

public sealed class IbkrTrendContextProvider : ITrendContextProvider
{
    private readonly TrendMarketDataRepository _repo;
    private readonly TrendAnalysisSettings _settings;
    private readonly TimeZoneInfo _timeZone;

    public IbkrTrendContextProvider(
        TrendMarketDataRepository repo,
        TrendAnalysisSettings settings)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeZone = ResolveTimeZone(settings.TrendRangeTimeZone);
    }

    public async Task<TrendContext?> GetTrendContextAsync(
        string exchange,
        string symbol,
        DateTime quoteTsUtc,
        CancellationToken ct = default)
    {
        if (!_settings.EnableTrendAnalysis)
            return null;

        if (_settings.TrendUseExplicitRange)
        {
            var rangePoints = await GetExplicitRangePointsAsync(
                exchange: exchange,
                symbol: symbol,
                quoteTsUtc: quoteTsUtc,
                ct: ct).ConfigureAwait(false);

            var ctx = TrendPriceMath.BuildContext(
                source: "IBKR",
                points: rangePoints,
                computedAtUtc: quoteTsUtc,
                settings: _settings);

            if (ctx is null || !_settings.LocalTrendQuality.Enabled)
                return ctx;

            var local = await BuildLocalQualityAsync(exchange, symbol, quoteTsUtc, ct).ConfigureAwait(false);
            return local is null ? ctx : ctx with { LocalQuality = local };
        }

        var minutes = Math.Max(1, _settings.TrendTimeWindowMinutes);
        var points = await _repo.GetIbkrMarketTicksByWindowAsync(
            exchange: exchange,
            symbol: symbol,
            window: TimeSpan.FromMinutes(minutes),
            asOfUtc: quoteTsUtc,
            ct: ct).ConfigureAwait(false);

        var trendCtx = TrendPriceMath.BuildContext(
            source: "IBKR",
            points: points,
            computedAtUtc: quoteTsUtc,
            settings: _settings);
        if (trendCtx is null || !_settings.LocalTrendQuality.Enabled)
            return trendCtx;

        var localQuality = await BuildLocalQualityAsync(exchange, symbol, quoteTsUtc, ct).ConfigureAwait(false);
        return localQuality is null ? trendCtx : trendCtx with { LocalQuality = localQuality };
    }

    private async Task<LocalTrendQualitySnapshot?> BuildLocalQualityAsync(
        string exchange,
        string symbol,
        DateTime quoteTsUtc,
        CancellationToken ct)
    {
        var lq = _settings.LocalTrendQuality;
        var magnitudeMinutes = Math.Max(1, lq.MagnitudeWindowMinutes);
        var chopMinutes = Math.Max(1, lq.ChopWindowMinutes);

        var magnitudePoints = await _repo.GetIbkrMarketTicksByWindowAsync(
            exchange: exchange,
            symbol: symbol,
            window: TimeSpan.FromMinutes(magnitudeMinutes),
            asOfUtc: quoteTsUtc,
            ct: ct).ConfigureAwait(false);

        var chopPoints = await _repo.GetIbkrMarketTicksByWindowAsync(
            exchange: exchange,
            symbol: symbol,
            window: TimeSpan.FromMinutes(chopMinutes),
            asOfUtc: quoteTsUtc,
            ct: ct).ConfigureAwait(false);

        return LocalTrendQualityMath.BuildSnapshot(
            source: "IBKR",
            computedAtUtc: quoteTsUtc,
            magnitudePoints: magnitudePoints,
            chopPoints: chopPoints,
            settings: lq);
    }

    private async Task<IReadOnlyList<Data.Models.TrendMarketDataPoint>> GetExplicitRangePointsAsync(
        string exchange,
        string symbol,
        DateTime quoteTsUtc,
        CancellationToken ct)
    {
        var lookbackMinutes = Math.Max(1, _settings.TrendUseExplicitRangeMins);
        var sessionStartLocal = _settings.GetStartLocalOrDefault(new TimeSpan(9, 30, 0));
        var sessionEndLocal = _settings.GetEndLocalOrDefault(new TimeSpan(16, 0, 0));
        var intervals = BuildSessionLookbackIntervalsUtcFromLocal(
            asOfUtc: quoteTsUtc,
            timeZone: _timeZone,
            sessionStartLocal: sessionStartLocal,
            sessionEndLocal: sessionEndLocal,
            lookback: TimeSpan.FromMinutes(lookbackMinutes));

        if (intervals.Count > 0)
        {
            var all = new List<Data.Models.TrendMarketDataPoint>();
            foreach (var (fromUtc, toUtc) in intervals)
            {
                var part = await _repo.GetIbkrMarketTicksByRangeAsync(
                    exchange: exchange,
                    symbol: symbol,
                    fromUtc: fromUtc,
                    toUtc: toUtc,
                    ct: ct).ConfigureAwait(false);
                all.AddRange(part);
            }

            if (all.Count > 0)
            {
                return all
                    .OrderBy(p => p.Utc)
                    .ToArray();
            }
        }

        // Fallback: original explicit range behavior, ali ogranicen max lookback-om.
        var (fromUtcFallback, toUtcFallback) = ResolveExplicitRangeUtc(quoteTsUtc);
        var hardCutoff = quoteTsUtc - TimeSpan.FromMinutes(lookbackMinutes);
        if (fromUtcFallback < hardCutoff)
            fromUtcFallback = hardCutoff;

        return await _repo.GetIbkrMarketTicksByRangeAsync(
            exchange: exchange,
            symbol: symbol,
            fromUtc: fromUtcFallback,
            toUtc: toUtcFallback,
            ct: ct).ConfigureAwait(false);
    }

    private static List<(DateTime FromUtc, DateTime ToUtc)> BuildSessionLookbackIntervalsUtcFromLocal(
        DateTime asOfUtc,
        TimeZoneInfo timeZone,
        TimeSpan sessionStartLocal,
        TimeSpan sessionEndLocal,
        TimeSpan lookback)
    {
        var result = new List<(DateTime FromUtc, DateTime ToUtc)>();
        var remaining = lookback;

        // Start from "today" in exchange-local time and walk backwards day by day.
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(asOfUtc, timeZone);
        var dayLocal = localNow.Date;
        for (var i = 0; i < 10 && remaining > TimeSpan.Zero; i++)
        {
            var sessionStartLocalDt = dayLocal + sessionStartLocal;
            var sessionEndLocalDt = dayLocal + sessionEndLocal;

            if (sessionEndLocalDt <= sessionStartLocalDt)
            {
                // Overnight sessions are not expected for IBKR RTH; skip invalid setup.
                dayLocal = dayLocal.AddDays(-1);
                continue;
            }

            var sessionStartUtc = TimeZoneInfo.ConvertTimeToUtc(sessionStartLocalDt, timeZone);
            var sessionEndUtc = TimeZoneInfo.ConvertTimeToUtc(sessionEndLocalDt, timeZone);

            var effectiveEnd = Min(sessionEndUtc, asOfUtc);
            if (effectiveEnd > sessionStartUtc)
            {
                var available = effectiveEnd - sessionStartUtc;
                var take = Min(available, remaining);
                var from = effectiveEnd - take;
                result.Add((from, effectiveEnd));
                remaining -= take;
            }

            dayLocal = dayLocal.AddDays(-1);
        }

        result.Reverse();
        return result;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;

    private (DateTime FromUtc, DateTime ToUtc) ResolveExplicitRangeUtc(DateTime quoteTsUtc)
    {
        var startLocal = _settings.GetStartLocalOrDefault(new TimeSpan(9, 30, 0));
        var endLocal = _settings.GetEndLocalOrDefault(new TimeSpan(16, 0, 0));

        var quoteLocal = TimeZoneInfo.ConvertTimeFromUtc(quoteTsUtc, _timeZone);
        var dateLocal = quoteLocal.Date;

        DateTime fromLocal;
        DateTime toLocal;

        if (startLocal <= endLocal)
        {
            // Same-day window, e.g. 09:30 -> 16:00
            fromLocal = dateLocal + startLocal;
            toLocal = dateLocal + endLocal;
        }
        else
        {
            // Overnight window, e.g. 20:00 -> 16:30 (next day)
            if (quoteLocal.TimeOfDay >= startLocal)
            {
                fromLocal = dateLocal + startLocal;
                toLocal = dateLocal.AddDays(1) + endLocal;
            }
            else
            {
                fromLocal = dateLocal.AddDays(-1) + startLocal;
                toLocal = dateLocal + endLocal;
            }
        }

        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(fromLocal, _timeZone);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(toLocal, _timeZone);

        // Never query in the future for current decision.
        if (toUtc > quoteTsUtc)
            toUtc = quoteTsUtc;

        if (fromUtc >= toUtc)
        {
            // Fallback safety to avoid invalid range.
            fromUtc = quoteTsUtc.AddHours(-1);
            toUtc = quoteTsUtc;
        }

        return (fromUtc, toUtc);
    }

    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // fallback below
            }
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
