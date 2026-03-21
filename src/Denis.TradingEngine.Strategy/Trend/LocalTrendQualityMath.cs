#nullable enable
using System;
using System.Collections.Generic;
using Denis.TradingEngine.Data.Models;

namespace Denis.TradingEngine.Strategy.Trend;

internal static class LocalTrendQualityMath
{
    public static LocalTrendQualitySnapshot? BuildSnapshot(
        string source,
        DateTime computedAtUtc,
        IReadOnlyList<TrendMarketDataPoint> magnitudePoints,
        IReadOnlyList<TrendMarketDataPoint> chopPoints,
        LocalTrendQualitySettings settings)
    {
        if (settings is null || !settings.Enabled)
            return null;

        var mag = BuildSeriesStats(magnitudePoints);
        var chop = BuildSeriesStats(chopPoints);

        if (mag is null || chop is null)
            return new LocalTrendQualitySnapshot(
                Enabled: true,
                DryRun: settings.DryRun,
                Source: source,
                ComputedAtUtc: computedAtUtc,
                MagnitudeWindowMinutes: Math.Max(1, settings.MagnitudeWindowMinutes),
                ChopWindowMinutes: Math.Max(1, settings.ChopWindowMinutes),
                MagnitudePoints: mag?.Count ?? 0,
                ChopPoints: chop?.Count ?? 0,
                MagnitudeNetMoveFraction: 0m,
                ChopNetMoveFraction: 0m,
                ChopRangeFraction: 0m,
                ChopPathFraction: 0m,
                ChopEfficiency: 0m,
                ChopToMagnitudeRatio: null,
                WouldPass: true,
                DecisionReason: "insufficient-data");

        if (mag.Count < Math.Max(2, settings.MagnitudeMinPoints) || chop.Count < Math.Max(2, settings.ChopMinPoints))
        {
            return new LocalTrendQualitySnapshot(
                Enabled: true,
                DryRun: settings.DryRun,
                Source: source,
                ComputedAtUtc: computedAtUtc,
                MagnitudeWindowMinutes: Math.Max(1, settings.MagnitudeWindowMinutes),
                ChopWindowMinutes: Math.Max(1, settings.ChopWindowMinutes),
                MagnitudePoints: mag.Count,
                ChopPoints: chop.Count,
                MagnitudeNetMoveFraction: mag.NetMoveFraction,
                ChopNetMoveFraction: chop.NetMoveFraction,
                ChopRangeFraction: chop.RangeFraction,
                ChopPathFraction: chop.PathFraction,
                ChopEfficiency: chop.Efficiency,
                ChopToMagnitudeRatio: null,
                WouldPass: true,
                DecisionReason: "insufficient-points");
        }

        var absMagnitude = Math.Abs(mag.NetMoveFraction);
        decimal? chopToMagnitude = null;
        if (absMagnitude > 0m)
            chopToMagnitude = chop.PathFraction / absMagnitude;

        var pass = true;
        var reasons = new List<string>(4);

        if (settings.RequirePositiveMagnitude && mag.NetMoveFraction <= 0m)
        {
            pass = false;
            reasons.Add("magnitude<=0");
        }

        if (settings.MinMagnitudeFraction > 0m && mag.NetMoveFraction < settings.MinMagnitudeFraction)
        {
            pass = false;
            reasons.Add($"mag<{settings.MinMagnitudeFraction:F6}");
        }

        if (settings.MinChopEfficiency > 0m && chop.Efficiency < settings.MinChopEfficiency)
        {
            pass = false;
            reasons.Add($"eff<{settings.MinChopEfficiency:F4}");
        }

        if (settings.MaxChopToMagnitudeRatio > 0m)
        {
            if (!chopToMagnitude.HasValue)
            {
                pass = false;
                reasons.Add("ratio=na");
            }
            else if (chopToMagnitude.Value > settings.MaxChopToMagnitudeRatio)
            {
                pass = false;
                reasons.Add($"ratio>{settings.MaxChopToMagnitudeRatio:F2}");
            }
        }

        return new LocalTrendQualitySnapshot(
            Enabled: true,
            DryRun: settings.DryRun,
            Source: source,
            ComputedAtUtc: computedAtUtc,
            MagnitudeWindowMinutes: Math.Max(1, settings.MagnitudeWindowMinutes),
            ChopWindowMinutes: Math.Max(1, settings.ChopWindowMinutes),
            MagnitudePoints: mag.Count,
            ChopPoints: chop.Count,
            MagnitudeNetMoveFraction: mag.NetMoveFraction,
            ChopNetMoveFraction: chop.NetMoveFraction,
            ChopRangeFraction: chop.RangeFraction,
            ChopPathFraction: chop.PathFraction,
            ChopEfficiency: chop.Efficiency,
            ChopToMagnitudeRatio: chopToMagnitude,
            WouldPass: pass,
            DecisionReason: pass ? "pass" : string.Join("|", reasons));
    }

    private static SeriesStats? BuildSeriesStats(IReadOnlyList<TrendMarketDataPoint> points)
    {
        if (points is null || points.Count == 0)
            return null;

        decimal? first = null;
        decimal? last = null;
        decimal? prev = null;
        decimal min = 0m;
        decimal max = 0m;
        decimal path = 0m;
        var count = 0;

        for (int i = 0; i < points.Count; i++)
        {
            var px = ResolvePrice(points[i]);
            if (!px.HasValue || px.Value <= 0m)
                continue;

            var p = px.Value;
            if (count == 0)
            {
                first = p;
                min = p;
                max = p;
            }
            else
            {
                if (p < min) min = p;
                if (p > max) max = p;
                if (prev.HasValue)
                    path += Math.Abs(p - prev.Value);
            }

            prev = p;
            last = p;
            count++;
        }

        if (count < 2 || !first.HasValue || !last.HasValue || first.Value <= 0m)
            return null;

        var netMove = (last.Value - first.Value) / first.Value;
        var rangeFrac = (max - min) / first.Value;
        var pathFrac = path / first.Value;
        var eff = path > 0m ? Math.Abs(last.Value - first.Value) / path : 0m;

        return new SeriesStats(count, netMove, rangeFrac, pathFrac, eff);
    }

    private static decimal? ResolvePrice(TrendMarketDataPoint p)
    {
        if (p.Price.HasValue)
            return p.Price.Value;
        if (p.Bid.HasValue && p.Ask.HasValue)
            return (p.Bid.Value + p.Ask.Value) / 2m;
        return p.Bid ?? p.Ask;
    }

    private sealed record SeriesStats(
        int Count,
        decimal NetMoveFraction,
        decimal RangeFraction,
        decimal PathFraction,
        decimal Efficiency);
}
