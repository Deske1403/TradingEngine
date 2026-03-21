#nullable enable
using Prometheus;

namespace Denis.TradingEngine.MetricsServer;

/// <summary>
/// Market data / feed metrike:
/// - broj primljenih kvotova po simbolu
/// - broj WS reconnect-ova po venue-u
/// - starenje (age) poslednjeg kvota u sekundama po simbolu
/// </summary>
public sealed class MarketFeedMetrics
{
    private static MarketFeedMetrics? _instance;
    private static readonly object Lock = new();

    private readonly Counter _quotesTotal;
    private readonly Counter _wsReconnectsTotal;
    private readonly Gauge _lastQuoteAgeSeconds;

    private MarketFeedMetrics()
    {
        // Koliko kvotova smo primili (cardinality = simbol x source, ali simboli su ti mali skup)
        _quotesTotal = Metrics.CreateCounter(
            "trading_quotes_total",
            "Total number of market quotes received",
            new[] { "symbol", "source" } // source: e.g. "ibkr", "sim", "replay"
        );

        // Koliko puta je WS morao da se reconnect-uje po venue-u (ibkr, kraken...)
        _wsReconnectsTotal = Metrics.CreateCounter(
            "trading_ws_reconnects_total",
            "Number of websocket reconnects",
            new[] { "venue" }
        );

        // “Age” poslednjeg kvota – koliko je stari u sekundama
        _lastQuoteAgeSeconds = Metrics.CreateGauge(
            "trading_last_quote_age_seconds",
            "Age of last received quote in seconds",
            new[] { "symbol" }
        );
    }

    public static MarketFeedMetrics Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (Lock)
                {
                    _instance ??= new MarketFeedMetrics();
                }
            }
            return _instance;
        }
    }

    // --- API za korišćenje ---

    /// <summary>
    /// Pozivaš iz mesta gde processuješ MarketQuote.
    /// </summary>
    public void ObserveQuote(string symbol, string source, double quoteAgeSeconds)
    {
        _quotesTotal
            .Labels(symbol, source)
            .Inc();

        _lastQuoteAgeSeconds
            .Labels(symbol)
            .Set(quoteAgeSeconds);
    }

    /// <summary>
    /// Pozivaš iz WS layer-a kad radiš reconnect.
    /// </summary>
    public void IncWsReconnect(string venue)
    {
        _wsReconnectsTotal
            .Labels(venue)
            .Inc();
    }
}