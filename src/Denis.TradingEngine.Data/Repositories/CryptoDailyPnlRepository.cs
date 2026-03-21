using Dapper;
using Serilog;
using Denis.TradingEngine.Logging.Discord;

namespace Denis.TradingEngine.Data.Repositories
{
    /// <summary>
    /// Repository za crypto daily PnL - zasebna tabela od IBKR daily_pnl
    /// </summary>
    public sealed class CryptoDailyPnlRepository
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ILogger _log;
        private readonly DiscordNotifier? _discordNotifier;
        private readonly string _exchangeName;

        public CryptoDailyPnlRepository(IDbConnectionFactory factory, ILogger? log = null, DiscordNotifier? discordNotifier = null, string exchangeName = "Crypto")
        {
            _factory = factory;
            _log = log ?? Log.ForContext<CryptoDailyPnlRepository>();
            _discordNotifier = discordNotifier;
            _exchangeName = exchangeName;
        }

        public async Task AddFeeAsync(DateTime utc, decimal feeUsd, CancellationToken ct = default)
        {
            var d = utc.Date;
            const string sql = @"
            INSERT INTO daily_pnl_crypto (trade_date, exchange, realized_pnl, total_fees, trade_count, updated_utc)
            VALUES (@TradeDate, @Exchange, 0, @Fee, 0, now())
            ON CONFLICT (trade_date, exchange)
            DO UPDATE SET
                total_fees = daily_pnl_crypto.total_fees + EXCLUDED.total_fees,
                updated_utc = now();";

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            await conn.ExecuteAsync(sql, new { TradeDate = d, Exchange = _exchangeName, Fee = feeUsd });
            _log.Information("[DB-CRYPTO-PNL] daily_pnl_crypto +fee {Date} {Exchange} {Fee}", d, _exchangeName, feeUsd);
        }

        public async Task AddTradeAsync(DateTime utc, decimal realizedPnlUsd, CancellationToken ct = default)
        {
            var d = utc.Date;
            _log.Information("[DB-CRYPTO-PNL-REPO] AddTradeAsync START date={Date} exchange={Exchange} pnl={Pnl:F2}", d, _exchangeName, realizedPnlUsd);
            
            try
            {
                const string sql = @"
                INSERT INTO daily_pnl_crypto (trade_date, exchange, realized_pnl, total_fees, trade_count, updated_utc)
                VALUES (@TradeDate, @Exchange, @Pnl, 0, 1, now())
                ON CONFLICT (trade_date, exchange)
                DO UPDATE SET
                    realized_pnl = daily_pnl_crypto.realized_pnl + EXCLUDED.realized_pnl,
                    trade_count  = daily_pnl_crypto.trade_count + 1,
                    updated_utc  = now();";

                await using var conn = await _factory.CreateOpenConnectionAsync(ct);
                var affected = await conn.ExecuteAsync(sql, new { TradeDate = d, Exchange = _exchangeName, Pnl = realizedPnlUsd });
                _log.Information("[DB-CRYPTO-PNL-REPO] daily_pnl_crypto +trade {Date} {Exchange} pnl={Pnl:F2} affected={Affected}", d, _exchangeName, realizedPnlUsd, affected);
                
                // Discord notification - read current values and send
                if (_discordNotifier != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            const string selectSql = @"
                                SELECT trade_date, realized_pnl, total_fees, trade_count
                                FROM daily_pnl_crypto
                                WHERE trade_date = @TradeDate AND exchange = @Exchange";
                            
                            await using var selectConn = await _factory.CreateOpenConnectionAsync(CancellationToken.None);
                            var result = await selectConn.QueryFirstOrDefaultAsync<(DateTime trade_date, decimal realized_pnl, decimal total_fees, int trade_count)>(
                                selectSql, new { TradeDate = d, Exchange = _exchangeName });
                            
                            if (result != default)
                            {
                                await _discordNotifier.NotifyDailyPnlAsync(
                                    date: result.trade_date,
                                    realizedPnl: result.realized_pnl,
                                    totalFees: result.total_fees,
                                    tradeCount: result.trade_count,
                                    exchange: _exchangeName,
                                    ct: CancellationToken.None);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Warning(ex, "[DISCORD] Failed to send Daily PnL notification for {Date} {Exchange}", d, _exchangeName);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[DB-CRYPTO-PNL-REPO-ERROR] AddTradeAsync failed date={Date} exchange={Exchange} pnl={Pnl:F2}", d, _exchangeName, realizedPnlUsd);
                throw;
            }
        }

        /// <summary>
        /// Vraća realizovani PnL za današnji dan iz daily_pnl_crypto tabele za ovaj exchange.
        /// </summary>
        public async Task<decimal> GetTodayRealizedPnlAsync(DateTime today, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT realized_pnl
                FROM daily_pnl_crypto
                WHERE trade_date = @Today AND exchange = @Exchange";

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            var result = await conn.QueryFirstOrDefaultAsync<decimal?>(sql, new { Today = today, Exchange = _exchangeName });
            return result ?? 0m;
        }
    }
}

