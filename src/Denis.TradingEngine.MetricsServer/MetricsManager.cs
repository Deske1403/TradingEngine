#nullable enable
using Prometheus;

namespace Denis.TradingEngine.MetricsServer;

public sealed class MetricsManager
{
    private static MetricsManager? _instance;
    private static readonly object Lock = new();

    private MetricServer? _server;

    private MetricsManager()
    {
        // namerno PRAZNO: sve metrike su u svojim klasama
    }

    public static MetricsManager Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (Lock)
                {
                    _instance ??= new MetricsManager();
                }
            }
            return _instance;
        }
    }

    public bool Start(int port = 1414)
    {
        try
        {

            _server = new MetricServer("localhost", port);
            _server.Start();
            return true;
        }
        catch(Exception ex)
        {
            Console.WriteLine(ex.ToString());
            return false;

        }
       
    }

    // Opcioni “shortcut” propertiji (ako želiš syntactic sugar):

    public AppMetrics App => AppMetrics.Instance;
    public DepthMetrics Depth => DepthMetrics.Instance;
    public OrderMetrics Orders => OrderMetrics.Instance;
    public StrategyMetrics Strategy => StrategyMetrics.Instance;
    public MarketFeedMetrics MArketFeed => MarketFeedMetrics.Instance;
}