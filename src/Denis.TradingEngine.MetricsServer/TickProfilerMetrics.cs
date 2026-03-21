#nullable enable
using Prometheus;

namespace Denis.TradingEngine.MetricsServer;

/// <summary>
/// Tick profiler metrike:
/// - ticks/sec (p50/p95) po simbolu i fazi
/// - quote age distribution po simbolu i fazi
/// - spread bps distribution po simbolu i fazi
/// - atrFrac distribution po simbolu i fazi
/// </summary>
public sealed class TickProfilerMetrics
{
    private static TickProfilerMetrics? _instance;
    private static readonly object Lock = new();

    private readonly Histogram _quoteAgeSeconds;
    private readonly Histogram _spreadBps;
    private readonly Histogram _atrFrac;
    private readonly Gauge _ticksPerSecondP50;
    private readonly Gauge _ticksPerSecondP95;

    private TickProfilerMetrics()
    {
        // Quote age distribution (u sekundama)
        _quoteAgeSeconds = Metrics.CreateHistogram(
            "tick_profiler_quote_age_seconds",
            "Distribution of quote age in seconds",
            new HistogramConfiguration
            {
                LabelNames = new[] { "symbol", "phase" },
                Buckets = new[] { 0.1, 0.5, 1.0, 2.0, 5.0, 10.0, 30.0, 60.0 }
            }
        );

        // Spread distribution (u bps)
        _spreadBps = Metrics.CreateHistogram(
            "tick_profiler_spread_bps",
            "Distribution of spread in basis points",
            new HistogramConfiguration
            {
                LabelNames = new[] { "symbol", "phase" },
                Buckets = new[] { 1.0, 2.0, 5.0, 10.0, 20.0, 50.0, 100.0, 200.0 }
            }
        );

        // ATR fraction distribution
        _atrFrac = Metrics.CreateHistogram(
            "tick_profiler_atr_frac",
            "Distribution of ATR fraction (ATR/Price)",
            new HistogramConfiguration
            {
                LabelNames = new[] { "symbol", "phase" },
                Buckets = new[] { 0.00001, 0.00005, 0.0001, 0.0002, 0.0005, 0.001, 0.002, 0.005 }
            }
        );

        // Tick rate percentiles (p50/p95)
        _ticksPerSecondP50 = Metrics.CreateGauge(
            "tick_profiler_ticks_per_second_p50",
            "50th percentile of ticks per second",
            new[] { "symbol", "phase" }
        );

        _ticksPerSecondP95 = Metrics.CreateGauge(
            "tick_profiler_ticks_per_second_p95",
            "95th percentile of ticks per second",
            new[] { "symbol", "phase" }
        );
    }

    public static TickProfilerMetrics Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (Lock)
                {
                    _instance ??= new TickProfilerMetrics();
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Snima tick sa quote informacijama.
    /// </summary>
    public void RecordTick(string symbol, string phase, double quoteAgeSeconds, double spreadBps, double atrFrac)
    {
        _quoteAgeSeconds.Labels(symbol, phase).Observe(quoteAgeSeconds);
        _spreadBps.Labels(symbol, phase).Observe(spreadBps);
        _atrFrac.Labels(symbol, phase).Observe(atrFrac);
    }

    /// <summary>
    /// Ažurira tick rate percentilne metrike.
    /// </summary>
    public void UpdateTickRate(string symbol, string phase, double p50, double p95)
    {
        _ticksPerSecondP50.Labels(symbol, phase).Set(p50);
        _ticksPerSecondP95.Labels(symbol, phase).Set(p95);
    }
}

