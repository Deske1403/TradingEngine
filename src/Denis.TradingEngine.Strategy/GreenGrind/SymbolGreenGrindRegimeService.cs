#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Denis.TradingEngine.Core.Trading;
using Serilog;

namespace Denis.TradingEngine.Strategy.GreenGrind;

public enum GreenGrindState
{
    Off = 0,
    Watch = 1,
    Active = 2,
    Strong = 3
}

public enum GreenGrindCandidateState
{
    Off = 0,
    Watch = 1,
    Active = 2,
    Strong = 3
}

public sealed record GreenGrindRuntimeSnapshot(
    string Symbol,
    bool Applies,
    bool Enabled,
    bool DryRun,
    GreenGrindState State,
    GreenGrindCandidateState CandidateState,
    string? InactiveReason,
    string? GrindId,
    DateTime? ActiveStartBucketUtc,
    DateTime AsOfUtc,
    int Rows3h,
    TimeSpan? Span3h,
    TimeSpan? MaxGap3h,
    decimal? NetBps3h,
    decimal? UpRatio3h,
    decimal? Eff3h,
    decimal? Pullback3h,
    int Trades3h,
    decimal? Imb3h,
    decimal? Spike3h,
    decimal? CtxPct)
{
    public bool IsActiveFamily => State is GreenGrindState.Active or GreenGrindState.Strong;
}

public sealed record GreenGrindStateTransition(
    string Symbol,
    GreenGrindState PreviousState,
    GreenGrindState NewState,
    GreenGrindRuntimeSnapshot Snapshot,
    string? Reason);

public sealed class SymbolGreenGrindRegimeService
{
    private readonly object _sync = new();
    private readonly GreenGrindSettings _settings;
    private readonly ILogger _log;
    private readonly Dictionary<string, SymbolRuntime> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _retain;

    public event Action<GreenGrindStateTransition>? StateChanged;

    public SymbolGreenGrindRegimeService(GreenGrindSettings settings, ILogger? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? Log.ForContext<SymbolGreenGrindRegimeService>();
        var contextMinutes = Math.Max(720, settings.ContextLookbackMinutes);
        _retain = TimeSpan.FromMinutes(contextMinutes + 60);
    }

    public bool IsEnabledOrDryRun => _settings.Enabled || _settings.DryRun;
    public bool IsHardGateEnabled => _settings.Enabled && !_settings.DryRun;
    public int BootstrapBarMinutes => Math.Max(1, _settings.BarMinutes);
    public TimeSpan BootstrapLookback
    {
        get
        {
            var minDuration = Math.Max(60, _settings.MinDurationMinutes);
            var contextMinutes = Math.Max(720, _settings.ContextLookbackMinutes);
            return TimeSpan.FromMinutes(contextMinutes + minDuration + 60);
        }
    }

    public void OnQuote(MarketQuote quote)
    {
        if (quote is null || quote.Symbol is null || string.IsNullOrWhiteSpace(quote.Symbol.Ticker))
            return;

        if (!quote.Mid.HasValue || quote.Mid.Value <= 0m)
            return;

        var tsUtc = quote.TimestampUtc == default ? DateTime.UtcNow : quote.TimestampUtc;
        var symbol = quote.Symbol.Ticker;
        var resolved = _settings.Resolve(symbol);
        if (!resolved.Enabled && !resolved.DryRun)
            return;

        lock (_sync)
        {
            var rt = GetOrCreate(symbol);
            var bucketTs = AlignBucket(tsUtc, resolved.BarMinutes);
            var bucket = GetOrCreateBucket(rt, bucketTs);
            bucket.MidSum += quote.Mid.Value;
            bucket.MidCount++;
            bucket.LastUpdateUtc = tsUtc;
            Prune(rt, tsUtc);
            rt.LastSnapshot = EvaluateLocked(symbol, rt, resolved, tsUtc);
        }
    }

    public void OnTrade(string symbol, DateTime utc, decimal quantity, bool isBuy)
    {
        if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0m)
            return;

        var tsUtc = utc == default ? DateTime.UtcNow : utc;
        var resolved = _settings.Resolve(symbol);
        if (!resolved.Enabled && !resolved.DryRun)
            return;

        lock (_sync)
        {
            var rt = GetOrCreate(symbol);
            var bucketTs = AlignBucket(tsUtc, resolved.BarMinutes);
            var bucket = GetOrCreateBucket(rt, bucketTs);
            bucket.TradeCount++;
            if (isBuy) bucket.BuyQty += quantity;
            else bucket.SellQty += quantity;
            bucket.LastUpdateUtc = tsUtc;
            Prune(rt, tsUtc);
            rt.LastSnapshot = EvaluateLocked(symbol, rt, resolved, tsUtc);
        }
    }

    public GreenGrindRuntimeSnapshot GetSnapshot(string symbol, DateTime asOfUtc)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return EmptySnapshot(symbol ?? string.Empty, _settings.Resolve(string.Empty), asOfUtc, "not-applicable");

        var resolved = _settings.Resolve(symbol);
        if (!resolved.Enabled && !resolved.DryRun)
            return EmptySnapshot(symbol, resolved, asOfUtc, "disabled");

        lock (_sync)
        {
            if (!_symbols.TryGetValue(symbol, out var rt))
                return EmptySnapshot(symbol, resolved, asOfUtc, "no-data");

            var tsUtc = asOfUtc == default ? DateTime.UtcNow : asOfUtc;
            rt.LastSnapshot = EvaluateLocked(symbol, rt, resolved, tsUtc);
            return rt.LastSnapshot;
        }
    }

    private static GreenGrindRuntimeSnapshot EmptySnapshot(string symbol, GreenGrindResolvedSettings resolved, DateTime asOfUtc, string reason)
        => new(
            symbol,
            resolved.Enabled || resolved.DryRun,
            resolved.Enabled,
            resolved.DryRun,
            GreenGrindState.Off,
            GreenGrindCandidateState.Off,
            reason,
            null,
            null,
            asOfUtc == default ? DateTime.UtcNow : asOfUtc,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            null,
            null,
            null);

    private SymbolRuntime GetOrCreate(string symbol)
    {
        if (_symbols.TryGetValue(symbol, out var rt))
            return rt;

        rt = new SymbolRuntime();
        _symbols[symbol] = rt;
        return rt;
    }

    private static DateTime AlignBucket(DateTime utc, int barMinutes)
    {
        if (utc.Kind != DateTimeKind.Utc)
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        var ticksPerBucket = TimeSpan.FromMinutes(barMinutes).Ticks;
        var alignedTicks = utc.Ticks - (utc.Ticks % ticksPerBucket);
        return new DateTime(alignedTicks, DateTimeKind.Utc);
    }

    private static Bucket GetOrCreateBucket(SymbolRuntime rt, DateTime bucketTs)
    {
        if (rt.Buckets.TryGetValue(bucketTs, out var b))
            return b;
        b = new Bucket { BucketUtc = bucketTs };
        rt.Buckets[bucketTs] = b;
        return b;
    }

    private void Prune(SymbolRuntime rt, DateTime nowUtc)
    {
        var cutoff = nowUtc - _retain;
        while (rt.Buckets.Count > 0)
        {
            var first = rt.Buckets.First();
            if (first.Key >= cutoff)
                break;
            rt.Buckets.Remove(first.Key);
        }
    }

    private GreenGrindRuntimeSnapshot EvaluateLocked(string symbol, SymbolRuntime rt, GreenGrindResolvedSettings cfg, DateTime nowUtc)
    {
        var currentBucket = AlignBucket(nowUtc, cfg.BarMinutes);
        var rows = rt.Buckets.Values
            // Use only closed buckets for state decisions; current in-progress bucket is too noisy.
            .Where(b => b.BucketUtc < currentBucket)
            .Where(b => b.MidCount > 0)
            .Select(b => new BucketRow(
                b.BucketUtc,
                b.MidSum / b.MidCount,
                b.TradeCount,
                b.BuyQty,
                b.SellQty))
            .OrderBy(r => r.T)
            .ToList();

        if (rows.Count == 0)
        {
            rt.State = GreenGrindState.Off;
            rt.ActiveStartBucketUtc = null;
            rt.GrindId = null;
            return EmptySnapshot(symbol, cfg, nowUtc, "no-data");
        }

        var eval = BuildEvaluation(rows, cfg);
        var prevState = rt.State;
        var nextState = ResolveState(prevState, eval, cfg);

        if (prevState != nextState && rt.LastStateChangeUtc.HasValue)
        {
            var elapsed = nowUtc - rt.LastStateChangeUtc.Value;
            var minHold = TimeSpan.FromMinutes(Math.Max(15, cfg.BarMinutes * 3));
            if (elapsed < minHold)
                nextState = prevState;
        }

        var wasActive = prevState is GreenGrindState.Active or GreenGrindState.Strong;
        var isActive = nextState is GreenGrindState.Active or GreenGrindState.Strong;

        GreenGrindStateTransition? transition = null;

        if (!wasActive && isActive)
        {
            rt.ActiveStartBucketUtc = eval.ActiveStartBucketUtc ?? rows[^1].T;
            rt.GrindId = $"{symbol}:{rt.ActiveStartBucketUtc:yyyyMMddHHmm}";
            _log.Information("[GREEN-GRIND] {Sym} ENTER {State} grindId={GrindId} net3h={NetBps:F1} up3h={UpRatio:F3} eff3h={Eff:F3}",
                symbol, nextState, rt.GrindId, eval.NetBps3h ?? 0m, eval.UpRatio3h ?? 0m, eval.Eff3h ?? 0m);
        }
        else if (wasActive && isActive && rt.ActiveStartBucketUtc is null && eval.ActiveStartBucketUtc.HasValue)
        {
            // Recovery safety: preserve stable grindId while already active.
            rt.ActiveStartBucketUtc = eval.ActiveStartBucketUtc.Value;
            rt.GrindId = $"{symbol}:{rt.ActiveStartBucketUtc:yyyyMMddHHmm}";
        }
        else if (wasActive && !isActive)
        {
            _log.Information("[GREEN-GRIND] {Sym} EXIT {Prev} -> {Next} reason={Reason} grindId={GrindId}",
                symbol, prevState, nextState, eval.InactiveReason ?? "n/a", rt.GrindId ?? "n/a");
            rt.ActiveStartBucketUtc = null;
            rt.GrindId = null;
        }
        else if (prevState != nextState)
        {
            _log.Information("[GREEN-GRIND] {Sym} STATE {Prev} -> {Next} candidate={Cand}", symbol, prevState, nextState, eval.CandidateState);
        }

        if (prevState != nextState)
            rt.LastStateChangeUtc = nowUtc;
        rt.State = nextState;

        var snapshot = new GreenGrindRuntimeSnapshot(
            Symbol: symbol,
            Applies: cfg.Enabled || cfg.DryRun,
            Enabled: cfg.Enabled,
            DryRun: cfg.DryRun,
            State: nextState,
            CandidateState: eval.CandidateState,
            InactiveReason: eval.InactiveReason,
            GrindId: rt.GrindId,
            ActiveStartBucketUtc: rt.ActiveStartBucketUtc,
            AsOfUtc: nowUtc,
            Rows3h: eval.Rows3h,
            Span3h: eval.Span3h,
            MaxGap3h: eval.MaxGap3h,
            NetBps3h: eval.NetBps3h,
            UpRatio3h: eval.UpRatio3h,
            Eff3h: eval.Eff3h,
            Pullback3h: eval.Pullback3h,
            Trades3h: eval.Trades3h,
            Imb3h: eval.Imb3h,
            Spike3h: eval.Spike3h,
            CtxPct: eval.ContextHighPct);

        if (prevState != nextState)
        {
            transition = new GreenGrindStateTransition(
                Symbol: symbol,
                PreviousState: prevState,
                NewState: nextState,
                Snapshot: snapshot,
                Reason: eval.InactiveReason);
        }

        if (transition is not null)
        {
            try { StateChanged?.Invoke(transition); } catch { }
        }

        return snapshot;
    }

    public void SeedMidBucket(string symbol, DateTime bucketUtc, decimal mid)
    {
        if (string.IsNullOrWhiteSpace(symbol) || mid <= 0m)
            return;

        var cfg = _settings.Resolve(symbol);
        if (!cfg.Enabled && !cfg.DryRun)
            return;

        lock (_sync)
        {
            var rt = GetOrCreate(symbol);
            var bucketTs = AlignBucket(bucketUtc.Kind == DateTimeKind.Utc ? bucketUtc : bucketUtc.ToUniversalTime(), cfg.BarMinutes);
            var bucket = GetOrCreateBucket(rt, bucketTs);
            bucket.MidSum = mid;
            bucket.MidCount = 1;
            if (bucket.LastUpdateUtc == default || bucket.LastUpdateUtc < bucketTs)
                bucket.LastUpdateUtc = bucketTs;
        }
    }

    public void SeedTradeBucket(string symbol, DateTime bucketUtc, int tradeCount, decimal buyQty, decimal sellQty)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        var cfg = _settings.Resolve(symbol);
        if (!cfg.Enabled && !cfg.DryRun)
            return;

        lock (_sync)
        {
            var rt = GetOrCreate(symbol);
            var bucketTs = AlignBucket(bucketUtc.Kind == DateTimeKind.Utc ? bucketUtc : bucketUtc.ToUniversalTime(), cfg.BarMinutes);
            var bucket = GetOrCreateBucket(rt, bucketTs);
            bucket.TradeCount = Math.Max(0, tradeCount);
            bucket.BuyQty = buyQty < 0m ? 0m : buyQty;
            bucket.SellQty = sellQty < 0m ? 0m : sellQty;
            if (bucket.LastUpdateUtc == default || bucket.LastUpdateUtc < bucketTs)
                bucket.LastUpdateUtc = bucketTs;
        }
    }

    public int RecomputeAll(DateTime asOfUtc)
    {
        var ts = asOfUtc == default ? DateTime.UtcNow : asOfUtc;
        var count = 0;
        lock (_sync)
        {
            foreach (var (symbol, rt) in _symbols)
            {
                var cfg = _settings.Resolve(symbol);
                if (!cfg.Enabled && !cfg.DryRun)
                    continue;

                Prune(rt, ts);
                rt.LastSnapshot = EvaluateLocked(symbol, rt, cfg, ts);
                count++;
            }
        }

        return count;
    }

    private static GreenGrindState ResolveState(GreenGrindState prev, Evaluation eval, GreenGrindResolvedSettings cfg)
    {
        var candidate = eval.CandidateState;

        switch (prev)
        {
            case GreenGrindState.Off:
            case GreenGrindState.Watch:
                if (candidate >= GreenGrindCandidateState.Active && PassActiveActivationHysteresis(eval, cfg))
                {
                    return candidate == GreenGrindCandidateState.Strong
                        ? GreenGrindState.Strong
                        : GreenGrindState.Active;
                }
                if (candidate >= GreenGrindCandidateState.Watch)
                    return GreenGrindState.Watch;
                return GreenGrindState.Off;

            case GreenGrindState.Active:
            case GreenGrindState.Strong:
                if (PassActiveDeactivationHysteresis(eval, cfg))
                {
                    if (candidate == GreenGrindCandidateState.Strong)
                        return GreenGrindState.Strong;
                    return GreenGrindState.Active;
                }
                return GreenGrindState.Off;

            default:
                return GreenGrindState.Off;
        }
    }

    private static bool PassActiveActivationHysteresis(Evaluation e, GreenGrindResolvedSettings cfg)
    {
        if (!e.Has3hWindow || !e.Coverage3hOk || !e.NoBreakdown3hOk || !e.ActiveContextOk)
            return false;

        if (!e.NetBps3h.HasValue || !e.UpRatio3h.HasValue || !e.Eff3h.HasValue)
            return false;

        return e.NetBps3h.Value >= cfg.ActivationThresholds.MinNetMoveBps
            && e.UpRatio3h.Value >= cfg.ActivationThresholds.MinUpRatio
            && e.Eff3h.Value >= cfg.ActivationThresholds.MinPathEfficiency;
    }

    private static bool PassActiveDeactivationHysteresis(Evaluation e, GreenGrindResolvedSettings cfg)
    {
        if (!e.Has3hWindow || !e.Coverage3hOk || !e.NoBreakdown3hOk || !e.ActiveContextOk)
            return false;

        if (!e.NetBps3h.HasValue || !e.UpRatio3h.HasValue || !e.Eff3h.HasValue)
            return false;

        return e.NetBps3h.Value >= cfg.DeactivationThresholds.MinNetMoveBps
            && e.UpRatio3h.Value >= cfg.DeactivationThresholds.MinUpRatio
            && e.Eff3h.Value >= cfg.DeactivationThresholds.MinPathEfficiency;
    }

    private static Evaluation BuildEvaluation(List<BucketRow> rows, GreenGrindResolvedSettings cfg)
    {
        var e = new Evaluation();
        if (rows.Count < 2)
        {
            e.InactiveReason = "no-data";
            return e;
        }

        var rollingWindow = TimeSpan.FromMinutes(Math.Max(cfg.BarMinutes, cfg.MinDurationMinutes));
        var minNetFraction = cfg.Watch.MinNetMoveBps / 10_000m;

        var upStep = new int[rows.Count];
        upStep[0] = 0;
        for (var i = 1; i < rows.Count; i++)
            upStep[i] = rows[i].Mid > rows[i - 1].Mid ? 1 : 0;

        var grindMask = new bool[rows.Count];
        WindowMetrics latest = WindowMetrics.Empty;

        for (var i = 0; i < rows.Count; i++)
        {
            var metrics = ComputeRollingMetricsAt(rows, upStep, i, rollingWindow);
            if (i == rows.Count - 1)
                latest = metrics;

            var coverageOk = CoverageOk(
                metrics,
                Math.Max(2, cfg.MinValidBuckets),
                rollingWindow,
                TimeSpan.FromMinutes(cfg.BarMinutes),
                cfg);

            var netOk = metrics.NetFraction.HasValue && metrics.NetFraction.Value >= minNetFraction;
            var upOk = metrics.UpRatio.HasValue && metrics.UpRatio.Value >= cfg.Watch.MinUpRatio;
            var rangeOk = metrics.RangeFraction.HasValue && metrics.RangeFraction.Value <= cfg.MaxRangeFraction;
            var flowOk = FlowOk(metrics, cfg);
            var nbd = NoBreakdownOk(metrics, cfg);
            var spikeOk = !metrics.SpikeConcentration.HasValue
                || metrics.SpikeConcentration.Value <= cfg.MaxSpikeConcentration;

            grindMask[i] = coverageOk && netOk && upOk && rangeOk && flowOk && nbd && spikeOk;
        }

        e.Rows3h = latest.Rows;
        e.Span3h = latest.Span;
        e.MaxGap3h = latest.MaxGap;
        e.Trades3h = latest.Trades;
        e.Imb3h = latest.Imbalance;
        e.NetBps3h = ToBps(latest.NetFraction);
        e.UpRatio3h = latest.UpRatio;
        e.Eff3h = latest.Efficiency;
        e.Pullback3h = latest.Pullback;
        e.Spike3h = latest.SpikeConcentration;

        var contextLookback = TimeSpan.FromMinutes(Math.Max(60, cfg.ContextLookbackMinutes));
        var contextWindowEnd = latest.StartT == default ? rows[^1].T : latest.StartT;
        var contextCutoff = contextWindowEnd - contextLookback;
        decimal contextHigh = 0m;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].T >= contextCutoff && rows[i].T < contextWindowEnd && rows[i].Mid > contextHigh)
                contextHigh = rows[i].Mid;
        }
        if (contextHigh > 0m)
        {
            e.ContextHigh = contextHigh;
            e.ContextHighPct = rows[^1].Mid / contextHigh;
        }

        e.Has3hWindow = latest.Rows >= Math.Max(2, cfg.MinValidBuckets);
        e.Coverage3hOk = CoverageOk(
            latest,
            Math.Max(2, cfg.MinValidBuckets),
            rollingWindow,
            TimeSpan.FromMinutes(cfg.BarMinutes),
            cfg);
        e.NoBreakdown3hOk = NoBreakdownOk(latest, cfg);
        e.ActiveContextOk = !e.ContextHighPct.HasValue || e.ContextHighPct.Value >= cfg.MinActiveContextHighPct;
        e.StrongContextOk = !e.ContextHighPct.HasValue || e.ContextHighPct.Value >= cfg.MinStrongContextHighPct;

        var lastIdx = rows.Count - 1;
        if (!grindMask[lastIdx])
        {
            e.CandidateState = GreenGrindCandidateState.Off;
            e.InactiveReason = DeriveInactiveReason(e.Coverage3hOk, latest, cfg, FlowOk(latest, cfg), e.NoBreakdown3hOk, e.ContextHighPct);
            return e;
        }

        e.ActiveStartBucketUtc = latest.StartT;
        e.InactiveReason = null;

        if (e.StrongContextOk && e.NoBreakdown3hOk && PassThresholds(latest, cfg.Strong))
            e.CandidateState = GreenGrindCandidateState.Strong;
        else if (e.ActiveContextOk && e.NoBreakdown3hOk && PassThresholds(latest, cfg.Active))
            e.CandidateState = GreenGrindCandidateState.Active;
        else
            e.CandidateState = GreenGrindCandidateState.Watch;

        return e;
    }

    private static WindowMetrics ComputeRollingMetricsAt(
        List<BucketRow> rows,
        int[] upStep,
        int endIdx,
        TimeSpan rollingWindow)
    {
        var endT = rows[endIdx].T;
        var startT = endT - rollingWindow;
        var startIdx = endIdx;
        while (startIdx > 0 && rows[startIdx - 1].T >= startT)
            startIdx--;

        var first = rows[startIdx];
        var last = rows[endIdx];
        decimal min = first.Mid;
        decimal max = first.Mid;
        decimal path = 0m;
        long trades = 0;
        decimal buy = 0m;
        decimal sell = 0m;
        int upCount = 0;
        TimeSpan? maxGap = null;
        decimal totalPosDelta = 0m;
        decimal maxPosDelta = 0m;

        for (var i = startIdx; i <= endIdx; i++)
        {
            var r = rows[i];
            if (r.Mid < min) min = r.Mid;
            if (r.Mid > max) max = r.Mid;
            trades += r.Trades;
            buy += r.BuyQty;
            sell += r.SellQty;
            upCount += upStep[i];

            if (i == startIdx)
                continue;

            var prev = rows[i - 1];
            var delta = r.Mid - prev.Mid;
            path += Math.Abs(delta);

            if (delta > 0m)
            {
                totalPosDelta += delta;
                if (delta > maxPosDelta)
                    maxPosDelta = delta;
            }

            var gap = r.T - prev.T;
            if (!maxGap.HasValue || gap > maxGap.Value)
                maxGap = gap;
        }

        var rowsCount = endIdx - startIdx + 1;
        var span = rowsCount >= 2 ? last.T - first.T : (TimeSpan?)null;
        var netFrac = first.Mid > 0m ? (last.Mid - first.Mid) / first.Mid : (decimal?)null;
        var eff = path > 0m ? Math.Abs(last.Mid - first.Mid) / path : (decimal?)null;
        var range = first.Mid > 0m ? (max - min) / first.Mid : (decimal?)null;
        var pullback = max > 0m ? (max - last.Mid) / max : (decimal?)null;
        var upRatio = rowsCount > 0 ? (decimal)upCount / rowsCount : (decimal?)null;
        var imb = (buy + sell) > 0m ? (buy - sell) / (buy + sell) : (decimal?)null;
        var spike = totalPosDelta > 0m ? maxPosDelta / totalPosDelta : (decimal?)null;

        return new WindowMetrics(
            Rows: rowsCount,
            StartT: first.T,
            EndT: last.T,
            Span: span,
            MaxGap: maxGap,
            NetFraction: netFrac,
            UpRatio: upRatio,
            Efficiency: eff,
            RangeFraction: range,
            Pullback: pullback,
            Trades: (int)Math.Min(int.MaxValue, trades),
            BuyQty: buy,
            SellQty: sell,
            Imbalance: imb,
            SpikeConcentration: spike);
    }

    private static bool SpikeOk(WindowMetrics w, GreenGrindResolvedSettings cfg)
    {
        return !w.SpikeConcentration.HasValue
            || w.SpikeConcentration.Value <= cfg.MaxSpikeConcentration;
    }

    private static string DeriveInactiveReason(bool coverage3, WindowMetrics w3, GreenGrindResolvedSettings cfg, bool flow3, bool nbd3, decimal? contextHighPct = null)
    {
        if (w3.Rows == 0)
            return "no-data";
        if (!coverage3)
        {
            if (!w3.Span.HasValue || w3.Span.Value < TimeSpan.FromMinutes(Math.Max(1, cfg.MinDurationMinutes - cfg.BarMinutes - cfg.SpanToleranceMinutes)))
                return "dur<window";
            return "coverage";
        }
        if (!nbd3)
            return "breakdown";
        if (!SpikeOk(w3, cfg))
            return "spike";
        if (contextHighPct.HasValue && contextHighPct.Value < cfg.MinActiveContextHighPct)
            return "context";
        if (!flow3)
            return "flow-fade";
        return "metrics";
    }

    private static bool FlowOk(WindowMetrics w, GreenGrindResolvedSettings cfg)
    {
        if (!cfg.RequireFlowConfirmation)
            return true;

        if (w.Trades < cfg.MinTrades3h)
            return false;

        return !w.Imbalance.HasValue || w.Imbalance.Value >= cfg.MinImbalance3h;
    }

    private static bool NoBreakdownOk(WindowMetrics w, GreenGrindResolvedSettings cfg)
    {
        if (!w.NetFraction.HasValue || !w.Pullback.HasValue)
            return false;
        if (w.NetFraction.Value <= 0m)
            return false;
        return w.Pullback.Value <= cfg.MaxPullbackFractionOfNet * w.NetFraction.Value;
    }

    private static bool PassThresholds(WindowMetrics w, GreenGrindThresholds t)
    {
        if (!w.NetFraction.HasValue || !w.UpRatio.HasValue || !w.Efficiency.HasValue)
            return false;

        var netBps = ToBps(w.NetFraction);
        return netBps.HasValue
            && netBps.Value >= t.MinNetMoveBps
            && w.UpRatio.Value >= t.MinUpRatio
            && w.Efficiency.Value >= t.MinPathEfficiency;
    }

    private static bool CoverageOk(WindowMetrics w, int minRows, TimeSpan duration, TimeSpan bar, GreenGrindResolvedSettings cfg)
    {
        if (w.Rows < minRows)
            return false;
        if (!w.Span.HasValue || !w.MaxGap.HasValue)
            return false;

        var expectedRows = Math.Max(2, (int)Math.Round(duration.TotalMinutes / bar.TotalMinutes, MidpointRounding.AwayFromZero));
        var expectedSpan = TimeSpan.FromTicks((expectedRows - 1L) * bar.Ticks);
        var tol = TimeSpan.FromMinutes(cfg.SpanToleranceMinutes);
        var minSpan = expectedSpan - tol;
        var maxSpan = expectedSpan + tol;

        return w.MaxGap.Value <= TimeSpan.FromMinutes(cfg.MaxGapMinutes)
            && w.Span.Value >= minSpan
            && w.Span.Value <= maxSpan;
    }

    private static decimal? ToBps(decimal? fraction) => fraction.HasValue ? fraction.Value * 10_000m : null;

    private static WindowMetrics ComputeWindow(List<BucketRow> rows, int maxRows)
    {
        if (rows.Count == 0)
            return WindowMetrics.Empty;

        var startIdx = Math.Max(0, rows.Count - maxRows);
        var slice = rows.Skip(startIdx).ToArray();
        if (slice.Length == 0)
            return WindowMetrics.Empty;

        var first = slice[0];
        var last = slice[^1];
        decimal min = first.Mid;
        decimal max = first.Mid;
        decimal path = 0m;
        int upCount = 0;
        int steps = 0;
        TimeSpan? maxGap = null;
        long trades = 0;
        decimal buy = 0m;
        decimal sell = 0m;
        decimal totalPosDelta = 0m;
        decimal maxPosDelta = 0m;

        for (var i = 0; i < slice.Length; i++)
        {
            var r = slice[i];
            if (r.Mid < min) min = r.Mid;
            if (r.Mid > max) max = r.Mid;
            trades += r.Trades;
            buy += r.BuyQty;
            sell += r.SellQty;

            if (i == 0) continue;

            var prev = slice[i - 1];
            var delta = r.Mid - prev.Mid;
            if (delta > 0m)
            {
                upCount++;
                totalPosDelta += delta;
                if (delta > maxPosDelta)
                    maxPosDelta = delta;
            }
            steps++;
            path += Math.Abs(delta);

            var gap = r.T - prev.T;
            if (!maxGap.HasValue || gap > maxGap.Value) maxGap = gap;
        }

        var span = slice.Length >= 2 ? last.T - first.T : (TimeSpan?)null;
        var netFrac = first.Mid > 0m ? (last.Mid - first.Mid) / first.Mid : (decimal?)null;
        var eff = path > 0m ? Math.Abs(last.Mid - first.Mid) / path : (decimal?)null;
        var range = first.Mid > 0m ? (max - min) / first.Mid : (decimal?)null;
        var pullback = max > 0m ? (max - last.Mid) / max : (decimal?)null;
        var upRatio = steps > 0 ? (decimal)upCount / steps : (decimal?)null;
        var imb = (buy + sell) > 0m ? (buy - sell) / (buy + sell) : (decimal?)null;
        var spike = totalPosDelta > 0m ? maxPosDelta / totalPosDelta : (decimal?)null;

        return new WindowMetrics(
            Rows: slice.Length,
            StartT: first.T,
            EndT: last.T,
            Span: span,
            MaxGap: maxGap,
            NetFraction: netFrac,
            UpRatio: upRatio,
            Efficiency: eff,
            RangeFraction: range,
            Pullback: pullback,
            Trades: (int)Math.Min(int.MaxValue, trades),
            BuyQty: buy,
            SellQty: sell,
            Imbalance: imb,
            SpikeConcentration: spike);
    }

    private sealed class SymbolRuntime
    {
        public SortedDictionary<DateTime, Bucket> Buckets { get; } = new();
        public GreenGrindState State { get; set; } = GreenGrindState.Off;
        public string? GrindId { get; set; }
        public DateTime? ActiveStartBucketUtc { get; set; }
        public DateTime? LastStateChangeUtc { get; set; }
        public GreenGrindRuntimeSnapshot? LastSnapshot { get; set; }
    }

    private sealed class Bucket
    {
        public DateTime BucketUtc { get; init; }
        public decimal MidSum { get; set; }
        public int MidCount { get; set; }
        public int TradeCount { get; set; }
        public decimal BuyQty { get; set; }
        public decimal SellQty { get; set; }
        public DateTime LastUpdateUtc { get; set; }
    }

    private sealed record BucketRow(DateTime T, decimal Mid, int Trades, decimal BuyQty, decimal SellQty);

    private sealed record WindowMetrics(
        int Rows,
        DateTime StartT,
        DateTime EndT,
        TimeSpan? Span,
        TimeSpan? MaxGap,
        decimal? NetFraction,
        decimal? UpRatio,
        decimal? Efficiency,
        decimal? RangeFraction,
        decimal? Pullback,
        int Trades,
        decimal BuyQty,
        decimal SellQty,
        decimal? Imbalance,
        decimal? SpikeConcentration)
    {
        public static WindowMetrics Empty { get; } = new(
            0, default, default, null, null, null, null, null, null, null, 0, 0m, 0m, null, null);
    }

    private sealed class Evaluation
    {
        public GreenGrindCandidateState CandidateState { get; set; } = GreenGrindCandidateState.Off;
        public string? InactiveReason { get; set; }
        public DateTime? ActiveStartBucketUtc { get; set; }
        public bool Has3hWindow { get; set; }
        public bool Coverage3hOk { get; set; }
        public bool NoBreakdown3hOk { get; set; }
        public bool ActiveContextOk { get; set; } = true;
        public bool StrongContextOk { get; set; } = true;

        public int Rows3h { get; set; }
        public TimeSpan? Span3h { get; set; }
        public TimeSpan? MaxGap3h { get; set; }
        public decimal? NetBps3h { get; set; }
        public decimal? UpRatio3h { get; set; }
        public decimal? Eff3h { get; set; }
        public decimal? Pullback3h { get; set; }
        public int Trades3h { get; set; }
        public decimal? Imb3h { get; set; }
        public decimal? Spike3h { get; set; }
        public decimal? ContextHighPct { get; set; }
        public decimal? ContextHigh { get; set; }
    }
}
