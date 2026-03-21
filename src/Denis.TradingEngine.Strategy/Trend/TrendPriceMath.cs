#nullable enable
using System;
using System.Collections.Generic;
using Denis.TradingEngine.Data.Models;

namespace Denis.TradingEngine.Strategy.Trend;

internal static class TrendPriceMath
{
    public static TrendContext? BuildContext(
        string source,
        IReadOnlyList<TrendMarketDataPoint> points,
        DateTime computedAtUtc,
        TrendAnalysisSettings settings,
        int? trendMinPointsOverride = null)
    {
        if (points == null || points.Count < 2)
            return null;

        var prices = new List<decimal>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            var px = ResolvePrice(points[i]);
            if (px.HasValue && px.Value > 0m)
                prices.Add(px.Value);
        }

        if (prices.Count < 2)
            return null;

        var minPoints = trendMinPointsOverride.HasValue && trendMinPointsOverride.Value > 0
            ? trendMinPointsOverride.Value
            : settings.TrendMinPoints;

        if (settings.TrendUseQualityScoring && prices.Count < Math.Max(2, minPoints))
            return null;

        var first = prices[0];
        var last = prices[prices.Count - 1];
        if (first <= 0m)
            return null;

        var endpointReturn = (last - first) / first;
        var score = endpointReturn;

        if (settings.TrendUseQualityScoring)
        {
            var slopeReturn = ComputeSlopeReturn(prices, first);
            var maxDrawdown = ComputeMaxDrawdownFraction(prices);
            var clampedDd = Math.Min(maxDrawdown, Math.Max(0m, settings.TrendMaxDrawdownClampFraction));

            var endpointW = settings.TrendEndpointWeight;
            var slopeW = settings.TrendSlopeWeight;
            var ddPenaltyW = settings.TrendDrawdownPenaltyWeight;

            score = (endpointW * endpointReturn) +
                    (slopeW * slopeReturn) -
                    (ddPenaltyW * clampedDd);
        }

        var threshold = Math.Max(0m, settings.TrendNeutralThresholdFraction);
        var direction = score > threshold
            ? TrendDirection.Up
            : score < -threshold
                ? TrendDirection.Down
                : TrendDirection.Neutral;

        return new TrendContext(
            Direction: direction,
            Score: score,
            Source: source,
            ComputedAtUtc: computedAtUtc);
    }

    private static decimal? ResolvePrice(TrendMarketDataPoint p)
    {
        if (p.Price.HasValue)
            return p.Price.Value;

        if (p.Bid.HasValue && p.Ask.HasValue)
            return (p.Bid.Value + p.Ask.Value) / 2m;

        return p.Bid ?? p.Ask;
    }

    private static decimal ComputeSlopeReturn(IReadOnlyList<decimal> prices, decimal firstPrice)
    {
        var n = prices.Count;
        if (n < 2 || firstPrice <= 0m)
            return 0m;

        decimal sumX = 0m;
        decimal sumY = 0m;
        decimal sumXY = 0m;
        decimal sumX2 = 0m;

        for (int i = 0; i < n; i++)
        {
            var x = i;
            var y = prices[i];
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
        }

        var denom = (n * sumX2) - (sumX * sumX);
        if (denom == 0m)
            return 0m;

        var slopePerStep = ((n * sumXY) - (sumX * sumY)) / denom;
        var projectedMove = slopePerStep * (n - 1);
        return projectedMove / firstPrice;
    }

    private static decimal ComputeMaxDrawdownFraction(IReadOnlyList<decimal> prices)
    {
        if (prices.Count == 0)
            return 0m;

        var peak = prices[0];
        decimal maxDd = 0m;

        for (int i = 1; i < prices.Count; i++)
        {
            var px = prices[i];
            if (px > peak)
            {
                peak = px;
                continue;
            }

            if (peak <= 0m)
                continue;

            var dd = (peak - px) / peak;
            if (dd > maxDd)
                maxDd = dd;
        }

        return maxDd;
    }
}
