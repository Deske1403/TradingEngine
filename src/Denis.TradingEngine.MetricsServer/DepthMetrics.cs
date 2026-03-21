#nullable enable
using Prometheus;

namespace Denis.TradingEngine.MetricsServer;

/// <summary>
/// Market-data metrike:
///  - tick rate (quotes per second)
///  - bid/ask spread
///  - stale quote detection
///  - mid-price levels
/// </summary>
public sealed class DepthMetrics
{
    private static DepthMetrics? _instance;
    private static readonly object Lock = new();

    private readonly Counter _ticksTotal;
    private readonly Counter _staleTicksTotal;

    private readonly Gauge _spreadBps;
    private readonly Gauge _midPrice;

    private readonly Gauge _ticksPerSecond;

    private DepthMetrics()
    {
        _ticksTotal = Metrics.CreateCounter(
            "md_ticks_total",
            "Total number of market-data ticks received",
            new[] { "symbol" }
        );

        _staleTicksTotal = Metrics.CreateCounter(
            "md_stale_ticks_total",
            "Ticks where TimestampUtc is older than threshold",
            new[] { "symbol" }
        );

        _spreadBps = Metrics.CreateGauge(
            "md_spread_bps",
            "Current spread in basis points",
            new[] { "symbol" }
        );

        _midPrice = Metrics.CreateGauge(
            "md_mid_price",
            "Last observed mid price",
            new[] { "symbol" }
        );

        _ticksPerSecond = Metrics.CreateGauge(
            "md_tick_rate_per_second",
            "Incoming tick rate (computed externally and set via code)",
            new[] { "symbol" }
        );
    }

    public static DepthMetrics Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (Lock)
                {
                    _instance ??= new DepthMetrics();
                }
            }
            return _instance;
        }
    }

    // --- API ---

    public void TickReceived(string symbol)
    {
        _ticksTotal.Labels(symbol).Inc();
    }

    public void StaleTick(string symbol)
    {
        _staleTicksTotal.Labels(symbol).Inc();
    }

    public void ObserveSpread(string symbol, double spreadBps)
    {
        _spreadBps.Labels(symbol).Set(spreadBps);
    }

    public void ObserveMid(string symbol, double mid)
    {
        _midPrice.Labels(symbol).Set(mid);
    }

    public void ObserveTickRate(string symbol, double tps)
    {
        _ticksPerSecond.Labels(symbol).Set(tps);
    }
}