#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Serilog;
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.Data.Repositories
{
    public sealed class TradeSignalRepository
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ILogger _log;

        public TradeSignalRepository(IDbConnectionFactory factory, ILogger? log = null)
        {
            _factory = factory;
            _log = log ?? Serilog.Log.ForContext<TradeSignalRepository>();
        }

        public async Task InsertAsync(
            DateTime utc,
            string symbol,
            string side,
            decimal? suggestedPrice,
            string? strategy,
            string? reason,
            bool accepted,
            string? rejectReason,
            decimal? plannedQty,
            decimal? plannedNotional,
            string? correlationId,
            string? runEnv,        // Paper / Real
            string? rthWindow,     // inside / outside / n/a
            string? tradingPhase = null,  // preRTH, open_1h, midday, etc. (auto-detected if null)
            string? exchange = null,
            CancellationToken ct = default)
        {
            // Auto-detect trading phase if not provided
            var phase = tradingPhase ?? TradingPhase.ToString(TradingPhase.GetPhase(utc));

            const string sql = @"
                INSERT INTO trade_signals
                (utc, symbol, side, suggested_price, strategy, reason,
                 accepted, reject_reason, planned_qty, planned_notional,
                 correlation_id, run_env, rth_window, trading_phase, exchange)
                VALUES
                (@Utc, @Symbol, @Side, @SuggestedPrice, @Strategy, @Reason,
                 @Accepted, @RejectReason, @PlannedQty, @PlannedNotional,
                 @CorrelationId, @RunEnv, @RthWindow, @TradingPhase, @Exchange);";

            var row = new
            {
                Utc = utc,
                Symbol = symbol,
                Side = side,
                SuggestedPrice = suggestedPrice,
                Strategy = strategy,
                Reason = reason,
                Accepted = accepted,
                RejectReason = rejectReason,
                PlannedQty = plannedQty,
                PlannedNotional = plannedNotional,
                CorrelationId = correlationId,
                RunEnv = runEnv,
                RthWindow = rthWindow,
                TradingPhase = phase,
                Exchange = exchange
            };

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            await conn.ExecuteAsync(sql, row);

            _log.Information(
                "[DB] trade_signals +1 {Sym} accepted={Acc} env={Env} rth={Rth} reason={R}",
                symbol,
                accepted,
                runEnv ?? "?",
                rthWindow ?? "?",
                rejectReason ?? reason ?? "n/a"
            );
        }

        /// <summary>
        /// Vraća trade counts po simbolu za današnji dan (samo accepted Buy signali).
        /// </summary>
        /// <param name="today">Današnji UTC datum</param>
        /// <param name="exchange">Opcioni exchange filter (npr. "SMART", "Kraken", "Bitfinex"). Ako je null, učitava sve exchange-e.</param>
        /// <param name="ct">Cancellation token</param>
        public async Task<Dictionary<string, int>> GetTodayTradeCountsPerSymbolAsync(DateTime today, string? exchange = null, CancellationToken ct = default)
        {
            var tomorrow = today.AddDays(1);
            var sql = @"
                SELECT symbol, COUNT(DISTINCT correlation_id) as count
                FROM trade_signals
                WHERE utc >= @Today AND utc < @Tomorrow
                  AND accepted = true
                  AND LOWER(side) = 'buy'
                  AND correlation_id IS NOT NULL";
            
            if (!string.IsNullOrWhiteSpace(exchange))
            {
                sql += " AND exchange = @Exchange";
            }
            
            sql += " GROUP BY symbol";

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            var results = await conn.QueryAsync<(string symbol, int count)>(
                sql, 
                new { Today = today, Tomorrow = tomorrow, Exchange = exchange });
            
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in results)
            {
                dict[row.symbol] = row.count;
            }
            
            return dict;
        }

        /// <summary>
        /// Vraća ukupan broj tradeova za današnji dan (samo accepted Buy signali).
        /// </summary>
        /// <param name="today">Današnji UTC datum</param>
        /// <param name="exchange">Opcioni exchange filter (npr. "SMART", "Kraken", "Bitfinex"). Ako je null, učitava sve exchange-e.</param>
        /// <param name="ct">Cancellation token</param>
        public async Task<int> GetTodayTradeCountTotalAsync(DateTime today, string? exchange = null, CancellationToken ct = default)
        {
            var tomorrow = today.AddDays(1);
            var sql = @"
                SELECT COUNT(DISTINCT correlation_id) as count
                FROM trade_signals
                WHERE utc >= @Today AND utc < @Tomorrow
                  AND accepted = true
                  AND LOWER(side) = 'buy'
                  AND correlation_id IS NOT NULL";
            
            if (!string.IsNullOrWhiteSpace(exchange))
            {
                sql += " AND exchange = @Exchange";
            }

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            var result = await conn.QueryFirstOrDefaultAsync<int?>(
                sql, 
                new { Today = today, Tomorrow = tomorrow, Exchange = exchange });
            return result ?? 0;
        }
    }
}