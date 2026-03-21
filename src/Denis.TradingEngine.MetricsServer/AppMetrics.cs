#nullable enable
using Prometheus;

namespace Denis.TradingEngine.MetricsServer;

/// <summary>
/// Core / app metrike:
/// - DB exception
/// - general exception
/// Ovo je ultra jednostavno – bez labela za početak.
/// </summary>
public sealed class AppMetrics
{
    private static AppMetrics? _instance;
    private static readonly object Lock = new();

    // Counters
    private readonly Counter _dbExceptions;
    private readonly Counter _generalExceptions;

    private AppMetrics()
    {
        _dbExceptions = Metrics.CreateCounter(
            "trading_db_exceptions_total",
            "Number of database exceptions in trading app");

        _generalExceptions = Metrics.CreateCounter(
            "trading_exceptions_total",
            "Number of general exceptions in trading app");
    }

    public static AppMetrics Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (Lock)
                {
                    _instance ??= new AppMetrics();
                }
            }
            return _instance;
        }
    }

    // --- API koje koristiš po kodu ---

    public void IncDbException()
        => _dbExceptions.Inc();

    public void IncGeneralException()
        => _generalExceptions.Inc();
}