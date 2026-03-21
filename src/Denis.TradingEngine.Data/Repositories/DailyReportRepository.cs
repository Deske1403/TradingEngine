#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Serilog;

namespace Denis.TradingEngine.Data.Repositories
{
    /// <summary>
    /// Repository za generisanje daily report-a po trading fazama.
    /// </summary>
    public sealed class DailyReportRepository
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ILogger _log;

        public DailyReportRepository(IDbConnectionFactory factory, ILogger? log = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _log = log ?? Serilog.Log.ForContext<DailyReportRepository>();
        }

        /// <summary>
        /// Generiše daily report za dati datum (po fazama).
        /// </summary>
        public async Task<DailyReport> GenerateReportAsync(DateTime tradeDate, CancellationToken ct = default)
        {
            var startUtc = tradeDate.Date;
            var endUtc = startUtc.AddDays(1);

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);

            // Signals po fazama
            var signalsByPhase = await conn.QueryAsync<PhaseStats>(@"
                SELECT 
                    trading_phase as Phase,
                    COUNT(*) as Total,
                    COUNT(*) FILTER (WHERE accepted = true) as Accepted,
                    COUNT(*) FILTER (WHERE accepted = false) as Rejected
                FROM trade_signals
                WHERE utc >= @StartUtc AND utc < @EndUtc
                GROUP BY trading_phase
                ORDER BY trading_phase",
                new { StartUtc = startUtc, EndUtc = endUtc });

            // Top blockers po fazi
            var topBlockers = await conn.QueryAsync<TopBlocker>(@"
                SELECT 
                    trading_phase as Phase,
                    reject_reason as Reason,
                    COUNT(*) as Count
                FROM trade_signals
                WHERE utc >= @StartUtc AND utc < @EndUtc
                  AND accepted = false
                  AND reject_reason IS NOT NULL
                GROUP BY trading_phase, reject_reason
                ORDER BY trading_phase, COUNT(*) DESC",
                new { StartUtc = startUtc, EndUtc = endUtc });

            // SignalSlayer rejections po fazi
            var slayerRejections = await conn.QueryAsync<PhaseStats>(@"
                SELECT 
                    trading_phase as Phase,
                    COUNT(*) as Total,
                    COUNT(*) FILTER (WHERE accepted = true) as Accepted,
                    COUNT(*) FILTER (WHERE accepted = false) as Rejected
                FROM signal_slayer_decisions
                WHERE utc >= @StartUtc AND utc < @EndUtc
                GROUP BY trading_phase
                ORDER BY trading_phase",
                new { StartUtc = startUtc, EndUtc = endUtc });

            // Top SignalSlayer rejection reasons po fazi
            var slayerTopReasons = await conn.QueryAsync<TopBlocker>(@"
                SELECT 
                    trading_phase as Phase,
                    reason_code as Reason,
                    COUNT(*) as Count
                FROM signal_slayer_decisions
                WHERE utc >= @StartUtc AND utc < @EndUtc
                  AND accepted = false
                GROUP BY trading_phase, reason_code
                ORDER BY trading_phase, COUNT(*) DESC",
                new { StartUtc = startUtc, EndUtc = endUtc });

            return new DailyReport
            {
                TradeDate = tradeDate,
                SignalsByPhase = signalsByPhase.ToList(),
                TopBlockers = topBlockers.ToList(),
                SlayerRejectionsByPhase = slayerRejections.ToList(),
                SlayerTopReasons = slayerTopReasons.ToList()
            };
        }
    }

    public sealed class DailyReport
    {
        public DateTime TradeDate { get; init; }
        public List<PhaseStats> SignalsByPhase { get; init; } = new();
        public List<TopBlocker> TopBlockers { get; init; } = new();
        public List<PhaseStats> SlayerRejectionsByPhase { get; init; } = new();
        public List<TopBlocker> SlayerTopReasons { get; init; } = new();
    }

    public sealed class PhaseStats
    {
        public string? Phase { get; init; }
        public long Total { get; init; }
        public long Accepted { get; init; }
        public long Rejected { get; init; }
    }

    public sealed class TopBlocker
    {
        public string? Phase { get; init; }
        public string? Reason { get; init; }
        public long Count { get; init; }
    }
}

