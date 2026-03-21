#nullable enable
using Dapper;
using Serilog;
using Denis.TradingEngine.Core.Trading;


namespace Denis.TradingEngine.Data.Repositories
{
    public sealed class TradeJournalRepository
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ILogger _log = Log.ForContext<TradeJournalRepository>();

        public TradeJournalRepository(IDbConnectionFactory factory)
        {
            _factory = factory;
        }

        public async Task InsertAsync(TradeJournalEntry e, CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO trade_journal
                (utc, symbol, side, quantity, price, notional, realized_pnl, 
                 is_paper, is_exit, strategy, correlation_id, broker_order_id, 
                 estimated_fee_usd, planned_price, risk_fraction, atr_used, price_risk, exchange)
                VALUES
                (@Utc, @Symbol, @Side, @Quantity, @Price, @Notional, @RealizedPnl,
                 @IsPaper, @IsExit, @Strategy, @CorrelationId, @BrokerOrderId,
                 @EstimatedFeeUsd, @PlannedPrice, @RiskFraction, @AtrUsed, @PriceRisk, @Exchange);
            ";

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            await conn.ExecuteAsync(sql, new
            {
                e.Utc,
                e.Symbol,
                e.Side,
                e.Quantity,
                e.Price,
                e.Notional,
                e.RealizedPnl,
                e.IsPaper,
                e.IsExit,
                e.Strategy,
                e.CorrelationId,
                e.BrokerOrderId,
                e.EstimatedFeeUsd,
                e.PlannedPrice,
                e.RiskFraction,
                e.AtrUsed,
                e.PriceRisk,
                Exchange = e.Exchange
            });
            _log.Information("[DB] Trade journal entry inserted for {Sym}", e.Symbol);
        }
    }
}