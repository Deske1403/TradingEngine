#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Serilog;
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.Data.Repositories
{
    /// <summary>
    /// Repository for persisting SignalSlayer decisions (accepted/rejected) with reason codes.
    /// Enables historical analysis of rejection patterns and trends.
    /// </summary>
    public sealed class SignalSlayerDecisionRepository
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ILogger _log;

        public SignalSlayerDecisionRepository(IDbConnectionFactory factory, ILogger? log = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _log = log ?? Serilog.Log.ForContext<SignalSlayerDecisionRepository>();
        }

        /// <summary>
        /// Inserts a SignalSlayer decision record.
        /// Non-blocking: errors are logged but don't throw.
        /// </summary>
        public async Task InsertAsync(
            DateTime utc,
            string symbol,
            string strategy,
            bool accepted,
            string reasonCode,
            decimal? price = null,
            decimal? atr = null,
            decimal? atrFraction = null,
            decimal? spreadBps = null,
            int? activityTicks = null,
            string? regime = null,
            decimal? slope5 = null,
            decimal? slope20 = null,
            string? runEnv = null,
            string? tradingPhase = null,  // auto-detected if null
            string? exchange = null,
            CancellationToken ct = default)
        {
            // Auto-detect trading phase if not provided
            var phase = tradingPhase ?? TradingPhase.ToString(TradingPhase.GetPhase(utc));

            const string sql = @"
                INSERT INTO signal_slayer_decisions
                (utc, symbol, strategy, accepted, reason_code,
                 price, atr, atr_fraction, spread_bps, activity_ticks, regime,
                 slope5, slope20, run_env, trading_phase, exchange)
                VALUES
                (@Utc, @Symbol, @Strategy, @Accepted, @ReasonCode,
                 @Price, @Atr, @AtrFraction, @SpreadBps, @ActivityTicks, @Regime,
                 @Slope5, @Slope20, @RunEnv, @TradingPhase, @Exchange);";

            var row = new
            {
                Utc = utc,
                Symbol = symbol,
                Strategy = strategy,
                Accepted = accepted,
                ReasonCode = reasonCode,
                Price = price,
                Atr = atr,
                AtrFraction = atrFraction,
                SpreadBps = spreadBps,
                ActivityTicks = activityTicks,
                Regime = regime,
                Slope5 = slope5,
                Slope20 = slope20,
                RunEnv = runEnv,
                TradingPhase = phase,
                Exchange = exchange
            };

            try
            {
                await using var conn = await _factory.CreateOpenConnectionAsync(ct);
                await conn.ExecuteAsync(sql, row);
            }
            catch (Exception ex)
            {
                // Non-blocking: log but don't crash the strategy
                _log.Warning(ex,
                    "[DB] Failed to insert signal_slayer_decisions {Sym} accepted={Acc} reason={Reason}",
                    symbol, accepted, reasonCode);
            }
        }

        /// <summary>
        /// Inserts multiple decisions in a batch (for efficiency).
        /// </summary>
        public async Task InsertBatchAsync(
            System.Collections.Generic.IEnumerable<SignalSlayerDecisionRecord> records,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO signal_slayer_decisions
                (utc, symbol, strategy, accepted, reason_code,
                 price, atr, atr_fraction, spread_bps, activity_ticks, regime,
                 slope5, slope20, run_env, trading_phase, exchange)
                VALUES
                (@Utc, @Symbol, @Strategy, @Accepted, @ReasonCode,
                 @Price, @Atr, @AtrFraction, @SpreadBps, @ActivityTicks, @Regime,
                 @Slope5, @Slope20, @RunEnv, @TradingPhase, @Exchange);";

            try
            {
                await using var conn = await _factory.CreateOpenConnectionAsync(ct);
                await conn.ExecuteAsync(sql, records);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DB] Failed to batch insert signal_slayer_decisions");
            }
        }
    }

    /// <summary>
    /// Record for batch insertion of SignalSlayer decisions.
    /// </summary>
    public sealed record SignalSlayerDecisionRecord(
        DateTime Utc,
        string Symbol,
        string Strategy,
        bool Accepted,
        string ReasonCode,
        decimal? Price = null,
        decimal? Atr = null,
        decimal? AtrFraction = null,
        decimal? SpreadBps = null,
        int? ActivityTicks = null,
        string? Regime = null,
        decimal? Slope5 = null,
        decimal? Slope20 = null,
        string? RunEnv = null,
        string? TradingPhase = null,
        string? Exchange = null
    );
}

