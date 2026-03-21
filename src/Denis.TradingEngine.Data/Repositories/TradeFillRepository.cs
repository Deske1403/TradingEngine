using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Serilog;
using Denis.TradingEngine.Data;

namespace Denis.TradingEngine.Data.Repositories
{
    public sealed class TradeFillRepository
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ILogger _log;

        public TradeFillRepository(IDbConnectionFactory factory, ILogger? log = null)
        {
            _factory = factory;
            _log = log ?? Serilog.Log.ForContext<TradeFillRepository>();
        }

        public async Task InsertAsync(
            DateTime utc,
            string symbol,
            string side,
            decimal quantity,
            decimal price,
            decimal notional,
            decimal realizedPnl,
            bool isPaper,
            bool isExit,
            string? strategy,
            string? correlationId,
            string? brokerOrderId,
            decimal? estimatedFeeUsd,
            string? exchange = null,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO trade_fills
                (utc, symbol, side, quantity, price, notional, realized_pnl,
                 is_paper, is_exit, strategy, correlation_id, broker_order_id, estimated_fee_usd, exchange)
                VALUES
                (@Utc, @Symbol, @Side, @Quantity, @Price, @Notional, @RealizedPnl,
                 @IsPaper, @IsExit, @Strategy, @CorrelationId, @BrokerOrderId, @EstimatedFeeUsd, @Exchange);";

            var row = new
            {
                Utc = utc,
                Symbol = symbol,
                Side = side,
                Quantity = quantity,
                Price = price,
                Notional = notional,
                RealizedPnl = realizedPnl,
                IsPaper = isPaper,
                IsExit = isExit,
                Strategy = strategy,
                CorrelationId = correlationId,
                BrokerOrderId = brokerOrderId,
                EstimatedFeeUsd = estimatedFeeUsd,
                Exchange = exchange
            };

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            await conn.ExecuteAsync(sql, row);
            _log.Information("[DB] trade_fills +1 {Sym} {Side} {Qty}@{Px} realized_pnl={RealizedPnl}", symbol, side, quantity, price, realizedPnl);
        }
    }
}