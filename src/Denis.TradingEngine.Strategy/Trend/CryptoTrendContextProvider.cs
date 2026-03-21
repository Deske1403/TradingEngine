#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Data.Models;
using Denis.TradingEngine.Data.Repositories;

namespace Denis.TradingEngine.Strategy.Trend;

public sealed class CryptoTrendContextProvider : ITrendContextProvider, ITrendContextDiagnosticsProvider
{
    private readonly TrendMarketDataRepository _repo;
    private readonly TrendAnalysisSettings _settings;
    private readonly Func<string, string, int?>? _trendMinPointsOverrideResolver;

    public CryptoTrendContextProvider(
        TrendMarketDataRepository repo,
        TrendAnalysisSettings settings,
        Func<string, string, int?>? trendMinPointsOverrideResolver = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _trendMinPointsOverrideResolver = trendMinPointsOverrideResolver;
    }

    public async Task<TrendContext?> GetTrendContextAsync(
        string exchange,
        string symbol,
        DateTime quoteTsUtc,
        CancellationToken ct = default)
    {
        var result = await GetTrendContextDiagnosticsAsync(exchange, symbol, quoteTsUtc, ct).ConfigureAwait(false);
        return result.Context;
    }

    public async Task<TrendContextDiagnosticsResult> GetTrendContextDiagnosticsAsync(
        string exchange,
        string symbol,
        DateTime quoteTsUtc,
        CancellationToken ct = default)
    {
        if (!_settings.EnableTrendAnalysis)
            return new TrendContextDiagnosticsResult(
                Context: null,
                NoContextReason: "disabled",
                RawPointCount: 0,
                UsablePointCount: 0,
                RequiredMinPoints: 0);

        var minutes = Math.Max(1, _settings.TrendTimeWindowMinutes);
        var points = await _repo.GetCryptoTradesByWindowAsync(
            exchange: exchange,
            symbol: symbol,
            window: TimeSpan.FromMinutes(minutes),
            asOfUtc: quoteTsUtc,
            ct: ct).ConfigureAwait(false);

        var trendMinPointsOverride = _trendMinPointsOverrideResolver?.Invoke(exchange, symbol);
        var configuredMinPoints = trendMinPointsOverride.HasValue && trendMinPointsOverride.Value > 0
            ? trendMinPointsOverride.Value
            : _settings.TrendMinPoints;
        var requiredMinPoints = _settings.TrendUseQualityScoring
            ? Math.Max(2, configuredMinPoints)
            : 2;
        var rawPointCount = points.Count;
        var usablePointCount = CountUsablePrices(points);

        var ctx = TrendPriceMath.BuildContext(
            source: "CRYPTO",
            points: points,
            computedAtUtc: quoteTsUtc,
            settings: _settings,
            trendMinPointsOverride: trendMinPointsOverride);

        if (ctx is null)
        {
            return new TrendContextDiagnosticsResult(
                Context: null,
                NoContextReason: ResolveNoContextReason(rawPointCount, usablePointCount, requiredMinPoints),
                RawPointCount: rawPointCount,
                UsablePointCount: usablePointCount,
                RequiredMinPoints: requiredMinPoints);
        }

        if (_settings.LocalTrendQuality.Enabled)
        {
            var local = await BuildLocalQualityAsync(exchange, symbol, quoteTsUtc, ct).ConfigureAwait(false);
            if (local is not null)
                ctx = ctx with { LocalQuality = local };
        }

        return new TrendContextDiagnosticsResult(
            Context: ctx,
            NoContextReason: null,
            RawPointCount: rawPointCount,
            UsablePointCount: usablePointCount,
            RequiredMinPoints: requiredMinPoints);
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

        var magnitudePoints = await _repo.GetCryptoTradesByWindowAsync(
            exchange: exchange,
            symbol: symbol,
            window: TimeSpan.FromMinutes(magnitudeMinutes),
            asOfUtc: quoteTsUtc,
            ct: ct).ConfigureAwait(false);

        var chopPoints = await _repo.GetCryptoTradesByWindowAsync(
            exchange: exchange,
            symbol: symbol,
            window: TimeSpan.FromMinutes(chopMinutes),
            asOfUtc: quoteTsUtc,
            ct: ct).ConfigureAwait(false);

        return LocalTrendQualityMath.BuildSnapshot(
            source: "CRYPTO",
            computedAtUtc: quoteTsUtc,
            magnitudePoints: magnitudePoints,
            chopPoints: chopPoints,
            settings: lq);
    }

    private static string ResolveNoContextReason(int rawPointCount, int usablePointCount, int requiredMinPoints)
    {
        if (rawPointCount <= 0)
            return "no-data";

        if (usablePointCount < Math.Max(2, requiredMinPoints))
            return "insufficient-points";

        return "no-data";
    }

    private static int CountUsablePrices(IReadOnlyList<TrendMarketDataPoint> points)
    {
        if (points.Count == 0)
            return 0;

        var count = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var px = ResolvePrice(points[i]);
            if (px.HasValue && px.Value > 0m)
                count++;
        }

        return count;
    }

    private static decimal? ResolvePrice(TrendMarketDataPoint p)
    {
        if (p.Price.HasValue)
            return p.Price.Value;

        if (p.Bid.HasValue && p.Ask.HasValue)
            return (p.Bid.Value + p.Ask.Value) / 2m;

        return p.Bid ?? p.Ask;
    }
}
