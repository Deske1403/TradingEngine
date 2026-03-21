using Serilog;
using Serilog.Core;
using Serilog.Events;
namespace Denis.TradingEngine.Logging
{
    public static class AppLog
    {
        private static Logger? _logger;

        public static void Init(
            string appName = "TradingEngine",
            string env = "dev",
            LogEventLevel minLevel = LogEventLevel.Information,
            string? logsDir = "logs")
        {
            var dir = logsDir ?? "logs";
            Directory.CreateDirectory(dir); // ensure folder exists

            // Serilog rolling pattern: "name-.log" -> name-YYYYMMDD.log
            var path = Path.Combine(dir, $"{appName}-.log");

            var cfg = new LoggerConfiguration()
                .MinimumLevel.Is(minLevel)
                .Enrich.WithProperty("app", appName)
                .Enrich.WithProperty("env", env)
                .Enrich.FromLogContext()
                // FILE sink (daily rolling). NOTE: shared:true ⇒ buffered MUST be false
                .WriteTo.Async(a => a.File(
                    path: path,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    buffered: false, // ← važna izmena (zbog shared:true)
                    outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {app}/{env} {SourceContext} {Message:lj}{NewLine}{Exception}"
                ))
                // (optional) console sink for dev
                .WriteTo.Console(outputTemplate:
                    "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {app}/{env} {SourceContext} {Message:lj}{NewLine}{Exception}");

            _logger = cfg.CreateLogger();
            Log.Logger = _logger;

            Log.Information("Serilog initialized. File rolling at {Path}", path);
        }

        public static ILogger ForContext<T>() => Log.ForContext<T>();
        public static ILogger ForContext(Type t) => Log.ForContext(t);
    }
}
