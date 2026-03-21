#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Denis.TradingEngine.Strategy.GreenGrind;

public sealed class GreenGrindSettings
{
    public bool Enabled { get; set; } = true;
    public bool DryRun { get; set; } = true;

    public int BarMinutes { get; set; } = 15;
    public int MinDurationMinutes { get; set; } = 180; // ACTIVE = 3h
    public int MinValidBuckets { get; set; } = 10;
    public int MaxGapMinutes { get; set; } = 30;
    public int SpanToleranceMinutes { get; set; } = 15;

    public decimal MaxRangeFraction { get; set; } = 0.06m;
    public decimal MaxPullbackFractionOfNet { get; set; } = 0.50m;
    public decimal MaxSpikeConcentration { get; set; } = 0.25m;
    public decimal MinActiveContextHighPct { get; set; } = 0.985m;
    public decimal MinContextHighPct { get; set; } = 0.998m;
    public int ContextLookbackMinutes { get; set; } = 720;

    public bool RequireFlowConfirmation { get; set; }
    public int MinTrades3h { get; set; } = 0;
    public decimal MinImbalance3h { get; set; } = -0.02m;

    public GreenGrindThresholds Watch { get; set; } = new()
    {
        MinNetMoveBps = 60m,
        MinUpRatio = 0.58m,
        MinPathEfficiency = 0.42m
    };

    public GreenGrindThresholds Active { get; set; } = new()
    {
        MinNetMoveBps = 100m,
        MinUpRatio = 0.60m,
        MinPathEfficiency = 0.45m
    };

    public GreenGrindThresholds Strong { get; set; } = new()
    {
        MinNetMoveBps = 160m,
        MinUpRatio = 0.62m,
        MinPathEfficiency = 0.50m
    };

    public GreenGrindThresholds ActivationThresholds { get; set; } = new()
    {
        MinNetMoveBps = 120m,
        MinUpRatio = 0.60m,
        MinPathEfficiency = 0.45m
    };

    public GreenGrindThresholds DeactivationThresholds { get; set; } = new()
    {
        MinNetMoveBps = 60m,
        MinUpRatio = 0.52m,
        MinPathEfficiency = 0.35m
    };

    public string Scope { get; set; } = "Symbol";

    public Dictionary<string, GreenGrindSymbolOverride> Symbols { get; set; } = new();

    public GreenGrindResolvedSettings Resolve(string symbol)
    {
        var result = new GreenGrindResolvedSettings
        {
            Symbol = symbol ?? string.Empty,
            Enabled = Enabled,
            DryRun = DryRun,
            BarMinutes = Math.Max(1, BarMinutes),
            MinDurationMinutes = Math.Max(BarMinutes, MinDurationMinutes),
            MinValidBuckets = Math.Max(1, MinValidBuckets),
            MaxGapMinutes = Math.Max(BarMinutes, MaxGapMinutes),
            SpanToleranceMinutes = Math.Max(0, SpanToleranceMinutes),
            MaxRangeFraction = MaxRangeFraction > 0m ? MaxRangeFraction : 0.06m,
            MaxPullbackFractionOfNet = MaxPullbackFractionOfNet > 0m ? MaxPullbackFractionOfNet : 0.50m,
            MaxSpikeConcentration = MaxSpikeConcentration > 0m ? MaxSpikeConcentration : 0.25m,
            MinActiveContextHighPct = MinActiveContextHighPct > 0m ? MinActiveContextHighPct : 0.985m,
            MinStrongContextHighPct = MinContextHighPct > 0m ? MinContextHighPct : 0.998m,
            ContextLookbackMinutes = Math.Max(60, ContextLookbackMinutes),
            RequireFlowConfirmation = RequireFlowConfirmation,
            MinTrades3h = Math.Max(0, MinTrades3h),
            MinImbalance3h = MinImbalance3h,
            Scope = string.IsNullOrWhiteSpace(Scope) ? "Symbol" : Scope.Trim(),
            Watch = Watch.CloneOrDefault(60m, 0.58m, 0.42m),
            Active = Active.CloneOrDefault(100m, 0.60m, 0.45m),
            Strong = Strong.CloneOrDefault(160m, 0.62m, 0.50m),
            ActivationThresholds = ActivationThresholds.CloneOrDefault(120m, 0.60m, 0.45m),
            DeactivationThresholds = DeactivationThresholds.CloneOrDefault(60m, 0.52m, 0.35m)
        };

        if (Symbols is null || string.IsNullOrWhiteSpace(symbol))
            return result;

        var kv = Symbols.FirstOrDefault(p => string.Equals(p.Key, symbol, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
            return result;

        var ov = kv.Value;
        if (ov.Enabled.HasValue) result.Enabled = ov.Enabled.Value;
        if (ov.DryRun.HasValue) result.DryRun = ov.DryRun.Value;
        if (ov.MinDurationMinutes.HasValue) result.MinDurationMinutes = Math.Max(result.BarMinutes, ov.MinDurationMinutes.Value);
        if (ov.MinValidBuckets.HasValue) result.MinValidBuckets = Math.Max(1, ov.MinValidBuckets.Value);
        if (ov.MaxGapMinutes.HasValue) result.MaxGapMinutes = Math.Max(result.BarMinutes, ov.MaxGapMinutes.Value);
        if (ov.SpanToleranceMinutes.HasValue) result.SpanToleranceMinutes = Math.Max(0, ov.SpanToleranceMinutes.Value);
        if (ov.MaxRangeFraction.HasValue && ov.MaxRangeFraction.Value > 0m) result.MaxRangeFraction = ov.MaxRangeFraction.Value;
        if (ov.MaxPullbackFractionOfNet.HasValue && ov.MaxPullbackFractionOfNet.Value > 0m) result.MaxPullbackFractionOfNet = ov.MaxPullbackFractionOfNet.Value;
        if (ov.MaxSpikeConcentration.HasValue && ov.MaxSpikeConcentration.Value > 0m) result.MaxSpikeConcentration = ov.MaxSpikeConcentration.Value;
        if (ov.MinActiveContextHighPct.HasValue && ov.MinActiveContextHighPct.Value > 0m) result.MinActiveContextHighPct = ov.MinActiveContextHighPct.Value;
        if (ov.MinContextHighPct.HasValue && ov.MinContextHighPct.Value > 0m) result.MinStrongContextHighPct = ov.MinContextHighPct.Value;
        if (ov.ContextLookbackMinutes.HasValue) result.ContextLookbackMinutes = Math.Max(60, ov.ContextLookbackMinutes.Value);
        if (ov.RequireFlowConfirmation.HasValue) result.RequireFlowConfirmation = ov.RequireFlowConfirmation.Value;
        if (ov.MinTrades3h.HasValue) result.MinTrades3h = Math.Max(0, ov.MinTrades3h.Value);
        if (ov.MinImbalance3h.HasValue) result.MinImbalance3h = ov.MinImbalance3h.Value;

        if (ov.Watch is not null) result.Watch = ov.Watch.Merge(result.Watch);
        if (ov.Active is not null) result.Active = ov.Active.Merge(result.Active);
        if (ov.Strong is not null) result.Strong = ov.Strong.Merge(result.Strong);
        if (ov.ActivationThresholds is not null) result.ActivationThresholds = ov.ActivationThresholds.Merge(result.ActivationThresholds);
        if (ov.DeactivationThresholds is not null) result.DeactivationThresholds = ov.DeactivationThresholds.Merge(result.DeactivationThresholds);

        return result;
    }
}

public sealed class GreenGrindSymbolOverride
{
    public bool? Enabled { get; set; }
    public bool? DryRun { get; set; }
    public int? MinDurationMinutes { get; set; }
    public int? MinValidBuckets { get; set; }
    public int? MaxGapMinutes { get; set; }
    public int? SpanToleranceMinutes { get; set; }
    public decimal? MaxRangeFraction { get; set; }
    public decimal? MaxPullbackFractionOfNet { get; set; }
    public decimal? MaxSpikeConcentration { get; set; }
    public decimal? MinActiveContextHighPct { get; set; }
    public decimal? MinContextHighPct { get; set; }
    public int? ContextLookbackMinutes { get; set; }
    public bool? RequireFlowConfirmation { get; set; }
    public int? MinTrades3h { get; set; }
    public decimal? MinImbalance3h { get; set; }

    public GreenGrindThresholds? Watch { get; set; }
    public GreenGrindThresholds? Active { get; set; }
    public GreenGrindThresholds? Strong { get; set; }
    public GreenGrindThresholds? ActivationThresholds { get; set; }
    public GreenGrindThresholds? DeactivationThresholds { get; set; }
}

public sealed class GreenGrindThresholds
{
    public decimal MinNetMoveBps { get; set; }
    public decimal MinUpRatio { get; set; }
    public decimal MinPathEfficiency { get; set; }

    public GreenGrindThresholds CloneOrDefault(decimal netBps, decimal upRatio, decimal eff)
    {
        return new GreenGrindThresholds
        {
            MinNetMoveBps = MinNetMoveBps != 0m ? MinNetMoveBps : netBps,
            MinUpRatio = MinUpRatio != 0m ? MinUpRatio : upRatio,
            MinPathEfficiency = MinPathEfficiency != 0m ? MinPathEfficiency : eff
        };
    }

    public GreenGrindThresholds Merge(GreenGrindThresholds fallback)
    {
        return new GreenGrindThresholds
        {
            MinNetMoveBps = MinNetMoveBps != 0m ? MinNetMoveBps : fallback.MinNetMoveBps,
            MinUpRatio = MinUpRatio != 0m ? MinUpRatio : fallback.MinUpRatio,
            MinPathEfficiency = MinPathEfficiency != 0m ? MinPathEfficiency : fallback.MinPathEfficiency
        };
    }
}

public sealed class GreenGrindResolvedSettings
{
    public string Symbol { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool DryRun { get; set; }
    public int BarMinutes { get; set; }
    public int MinDurationMinutes { get; set; }
    public int MinValidBuckets { get; set; }
    public int MaxGapMinutes { get; set; }
    public int SpanToleranceMinutes { get; set; }
    public decimal MaxRangeFraction { get; set; }
    public decimal MaxPullbackFractionOfNet { get; set; }
    public decimal MaxSpikeConcentration { get; set; }
    public decimal MinActiveContextHighPct { get; set; }
    public decimal MinStrongContextHighPct { get; set; }
    public int ContextLookbackMinutes { get; set; }
    public bool RequireFlowConfirmation { get; set; }
    public int MinTrades3h { get; set; }
    public decimal MinImbalance3h { get; set; }
    public string Scope { get; set; } = "Symbol";
    public GreenGrindThresholds Watch { get; set; } = new();
    public GreenGrindThresholds Active { get; set; } = new();
    public GreenGrindThresholds Strong { get; set; } = new();
    public GreenGrindThresholds ActivationThresholds { get; set; } = new();
    public GreenGrindThresholds DeactivationThresholds { get; set; } = new();
}
