using Prometheus;

namespace Denis.TradingEngine.MetricsServer;

/// <summary>
/// Metrike za order lifecycle:
///  - koliko BUY/SELL signala je emitovano
///  - koliko ih je uspešno ispunjeno
///  - rejectovi
///  - slippage
///  - TP/SL exiti
///  - latency fill-a
/// </summary>
public sealed class OrderMetrics
{
    private static OrderMetrics? _instance;
    private static readonly object Lock = new();

    private readonly Counter _buySignals;
    private readonly Counter _sellSignals;

    private readonly Counter _buyFilled;
    private readonly Counter _sellFilled;

    private readonly Counter _orderRejects;

    private readonly Counter _tpExits;
    private readonly Counter _slExits;

    private readonly Gauge _slippage;          // difference between expected and actual fill
    private readonly Gauge _fillLatencyMs;     // fill latency in milliseconds

    private readonly Gauge _openPositions;     // how many positions currently open

    private readonly Counter _filledNotional;  // total traded notional

    private OrderMetrics()
    {
        _buySignals = Metrics.CreateCounter(
            "orders_signal_buy_total",
            "Number of BUY signals emitted",
            new[] { "symbol" }
        );

        _sellSignals = Metrics.CreateCounter(
            "orders_signal_sell_total",
            "Number of SELL signals emitted",
            new[] { "symbol" }
        );

        _buyFilled = Metrics.CreateCounter(
            "orders_filled_buy_total",
            "Successful BUY fills",
            new[] { "symbol" }
        );

        _sellFilled = Metrics.CreateCounter(
            "orders_filled_sell_total",
            "Successful SELL fills",
            new[] { "symbol" }
        );

        _orderRejects = Metrics.CreateCounter(
            "orders_rejected_total",
            "Rejected / failed orders",
            new[] { "symbol" }
        );

        _tpExits = Metrics.CreateCounter(
            "orders_exit_tp_total",
            "Take-profit exits",
            new[] { "symbol" }
        );

        _slExits = Metrics.CreateCounter(
            "orders_exit_sl_total",
            "Stop-loss exits",
            new[] { "symbol" }
        );

        _slippage = Metrics.CreateGauge(
            "orders_slippage",
            "Observed slippage between expected and actual fill price",
            new[] { "symbol" }
        );

        _fillLatencyMs = Metrics.CreateGauge(
            "orders_fill_latency_ms",
            "Latency from signal to executed fill in milliseconds",
            new[] { "symbol" }
        );

        _openPositions = Metrics.CreateGauge(
            "orders_open_positions",
            "Number of currently open positions per symbol",
            new[] { "symbol" }
        );

        _filledNotional = Metrics.CreateCounter(
            "orders_filled_notional_total",
            "Total traded notional (dollars)",
            new[] { "symbol" }
        );
    }

    public static OrderMetrics Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (Lock)
                {
                    _instance ??= new OrderMetrics();
                }
            }
            return _instance;
        }
    }

    // --- API Methods ---

    public void SignalBuy(string symbol) => _buySignals.Labels(symbol).Inc();
    public void SignalSell(string symbol) => _sellSignals.Labels(symbol).Inc();

    public void FilledBuy(string symbol, double notional)
    {
        _buyFilled.Labels(symbol).Inc();
        _filledNotional.Labels(symbol).Inc(notional);
    }

    public void FilledSell(string symbol, double notional)
    {
        _sellFilled.Labels(symbol).Inc();
        _filledNotional.Labels(symbol).Inc(notional);
    }

    public void Reject(string symbol) => _orderRejects.Labels(symbol).Inc();

    public void ExitTP(string symbol) => _tpExits.Labels(symbol).Inc();
    public void ExitSL(string symbol) => _slExits.Labels(symbol).Inc();

    public void SetSlippage(string symbol, double slip) =>
        _slippage.Labels(symbol).Set(slip);

    public void SetFillLatency(string symbol, double ms) =>
        _fillLatencyMs.Labels(symbol).Set(ms);

    public void SetOpenPositions(string symbol, double count) =>
        _openPositions.Labels(symbol).Set(count);
}