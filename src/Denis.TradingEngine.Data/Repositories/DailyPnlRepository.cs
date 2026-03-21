using Dapper;
using Serilog;
using Denis.TradingEngine.Logging.Discord;

namespace Denis.TradingEngine.Data.Repositories
{
    public sealed class DailyPnlRepository
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ILogger _log;
        private readonly DiscordNotifier? _discordNotifier;

        public DailyPnlRepository(IDbConnectionFactory factory, ILogger? log = null, DiscordNotifier? discordNotifier = null)
        {
            _factory = factory;
            _log = log ?? Log.ForContext<DailyPnlRepository>();
            _discordNotifier = discordNotifier;
        }

        public async Task AddFeeAsync(DateTime utc, decimal feeUsd, CancellationToken ct = default)
        {
            var d = utc.Date;
            const string sql = @"
            INSERT INTO daily_pnl (trade_date, realized_pnl, total_fees, trade_count, updated_utc)
            VALUES (@TradeDate, 0, @Fee, 0, now())
            ON CONFLICT (trade_date)
            DO UPDATE SET
                total_fees = daily_pnl.total_fees + EXCLUDED.total_fees,
                updated_utc = now();";

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            await conn.ExecuteAsync(sql, new { TradeDate = d, Fee = feeUsd });
            _log.Information("[DB] daily_pnl +fee {Date} {Fee}", d, feeUsd);
        }

        public async Task AddTradeAsync(DateTime utc, decimal realizedPnlUsd, CancellationToken ct = default)
        {
            var d = utc.Date;
            _log.Information("[DB-PNL-REPO] AddTradeAsync START date={Date} pnl={Pnl:F2}", d, realizedPnlUsd);
            
            try
            {
                const string sql = @"
                INSERT INTO daily_pnl (trade_date, realized_pnl, total_fees, trade_count, updated_utc)
                VALUES (@TradeDate, @Pnl, 0, 1, now())
                ON CONFLICT (trade_date)
                DO UPDATE SET
                    realized_pnl = daily_pnl.realized_pnl + EXCLUDED.realized_pnl,
                    trade_count  = daily_pnl.trade_count + 1,
                    updated_utc  = now();";

                await using var conn = await _factory.CreateOpenConnectionAsync(ct);
                var affected = await conn.ExecuteAsync(sql, new { TradeDate = d, Pnl = realizedPnlUsd });
                _log.Information("[DB-PNL-REPO] daily_pnl +trade {Date} pnl={Pnl:F2} affected={Affected}", d, realizedPnlUsd, affected);
                
                // Discord notification - read current values and send
                if (_discordNotifier != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            const string selectSql = @"
                                SELECT trade_date, realized_pnl, total_fees, trade_count
                                FROM daily_pnl
                                WHERE trade_date = @TradeDate";
                            
                            await using var selectConn = await _factory.CreateOpenConnectionAsync(CancellationToken.None);
                            var result = await selectConn.QueryFirstOrDefaultAsync<(DateTime trade_date, decimal realized_pnl, decimal total_fees, int trade_count)>(
                                selectSql, new { TradeDate = d });
                            
                            if (result != default)
                            {
                                await _discordNotifier.NotifyDailyPnlAsync(
                                    date: result.trade_date,
                                    realizedPnl: result.realized_pnl,
                                    totalFees: result.total_fees,
                                    tradeCount: result.trade_count,
                                    exchange: "SMART",
                                    ct: CancellationToken.None);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Warning(ex, "[DISCORD] Failed to send Daily PnL notification for {Date}", d);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[DB-PNL-REPO-ERROR] AddTradeAsync failed date={Date} pnl={Pnl:F2}", d, realizedPnlUsd);
                throw;
            }
        }

        /// <summary>
        /// Vraća realizovani PnL za današnji dan.
        /// </summary>
        public async Task<decimal> GetTodayRealizedPnlAsync(DateTime today, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT realized_pnl
                FROM daily_pnl
                WHERE trade_date = @Today";

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            var result = await conn.QueryFirstOrDefaultAsync<decimal?>(sql, new { Today = today });
            return result ?? 0m;
        }
    }
}