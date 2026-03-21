#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Logging;
using Serilog;

namespace Denis.TradingEngine.Data.Repositories
{
    public sealed class MarketTickRepository
    {
        private readonly PgConnectionFactory _factory;
        private readonly ILogger _log;

        // Jedan jedini shape koji koristi i single i batch.
        private const string InsertSql = @"
INSERT INTO market_ticks (utc, exchange, symbol, bid, ask, bid_size, ask_size)
VALUES (@Utc, @Exchange, @Symbol, @Bid, @Ask, @BidSize, @AskSize);
";

        public MarketTickRepository(PgConnectionFactory factory, ILogger log)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task InsertAsync(
            DateTime utc,
            string exchange,
            string symbol,
            decimal? bid,
            decimal? ask,
            decimal? bidSize,
            decimal? askSize,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            exchange = string.IsNullOrWhiteSpace(exchange) ? null : exchange.Trim();

            try
            {
                await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);

                await conn.ExecuteAsync(InsertSql, new
                {
                    Utc = utc,
                    Exchange = exchange,
                    Symbol = symbol,
                    Bid = bid,
                    Ask = ask,
                    BidSize = bidSize,
                    AskSize = askSize
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DB-TICKS] insert failed for {Ex}:{Sym}", exchange, symbol);
            }
        }

        public Task InsertFromQuoteAsync(MarketQuote q, CancellationToken ct = default)
        {
            return InsertAsync(
                utc: q.TimestampUtc,
                exchange: q.Symbol.Exchange,
                symbol: q.Symbol.Ticker,
                bid: q.Bid,
                ask: q.Ask,
                bidSize: q.BidSize,
                askSize: q.AskSize,
                ct: ct);
        }

        // Namerno uklonjeno iz "real" toka. Tickovi treba da idu kroz BoundedTickQueue (batch + bounded).
        // Ako baš želiš da ga zadržiš za debug, napravi ga internal + Obsolete.
        [Obsolete("Do not use in real flow. Use BoundedTickQueue + BatchInsertAsync instead.")]
        public void InsertFireAndForget(MarketQuote q)
        {
            try
            {
                _ = InsertFromQuoteAsync(q, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DB-TICKS] scheduling failed for {Ex}:{Sym}", q.Symbol.Exchange, q.Symbol.Ticker);
            }
        }

        public async Task BatchInsertAsync(IEnumerable<MarketQuote> ticks, CancellationToken ct)
        {
            if (ticks is null) return;

            // Materijalizuj jednom (da ne enumeriraš više puta)
            var list = ticks as IList<MarketQuote> ?? new List<MarketQuote>(ticks);
            if (list.Count == 0)
                return;

            try
            {
                var sql = new StringBuilder(256 + list.Count * 64);
                var parameters = new DynamicParameters();

                sql.Append("INSERT INTO market_ticks (utc, exchange, symbol, bid, ask, bid_size, ask_size) VALUES ");

                for (int i = 0; i < list.Count; i++)
                {
                    var q = list[i];
                    var ex = string.IsNullOrWhiteSpace(q.Symbol.Exchange) ? null : q.Symbol.Exchange.Trim();

                    sql.Append($"(@Utc{i}, @Exchange{i}, @Symbol{i}, @Bid{i}, @Ask{i}, @BidSize{i}, @AskSize{i}),");

                    parameters.Add($"Utc{i}", q.TimestampUtc);
                    parameters.Add($"Exchange{i}", ex);
                    parameters.Add($"Symbol{i}", q.Symbol.Ticker);
                    parameters.Add($"Bid{i}", q.Bid);
                    parameters.Add($"Ask{i}", q.Ask);
                    parameters.Add($"BidSize{i}", q.BidSize);
                    parameters.Add($"AskSize{i}", q.AskSize);
                }

                // skloni zadnji zarez
                sql.Length--;

                await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
                await conn.ExecuteAsync(sql.ToString(), parameters).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DB-TICKS] batch insert failed (count={Count})", list.Count);
            }
        }

        public async Task<IReadOnlyList<MarketTickMidBucketRow>> GetRecentMidBucketsAsync(
            string exchange,
            DateTime sinceUtc,
            int barMinutes,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(exchange))
                return Array.Empty<MarketTickMidBucketRow>();

            if (barMinutes <= 0)
                barMinutes = 15;

            const string sql = @"
SELECT
    symbol AS ""Symbol"",
    date_bin((@BarMinutes || ' minutes')::interval, utc, TIMESTAMPTZ '2000-01-01') AS ""BucketUtc"",
    AVG((bid + ask) / 2.0) AS ""Mid""
FROM market_ticks
WHERE exchange = @Exchange
  AND utc >= @SinceUtc
  AND bid IS NOT NULL
  AND ask IS NOT NULL
GROUP BY symbol, date_bin((@BarMinutes || ' minutes')::interval, utc, TIMESTAMPTZ '2000-01-01')
ORDER BY ""Symbol"", ""BucketUtc"";";

            try
            {
                await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
                var rows = await conn.QueryAsync<MarketTickMidBucketRow>(sql, new
                {
                    Exchange = exchange,
                    SinceUtc = sinceUtc,
                    BarMinutes = barMinutes
                }).ConfigureAwait(false);
                return rows.AsList();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DB-TICKS] failed loading recent mid buckets exchange={Exchange}", exchange);
                return Array.Empty<MarketTickMidBucketRow>();
            }
        }

        public async Task<GreenGrindDbSnapshotRow?> GetGreenGrindLatestSnapshotAsync(
            string exchange,
            string symbol,
            DateTime asOfUtc,
            int barMinutes,
            int rollingMinutes,
            int minValidBuckets,
            int maxGapMinutes,
            decimal minWatchNetFraction,
            decimal minWatchUpRatio,
            decimal minActiveNetFraction,
            decimal minActiveUpRatio,
            decimal minActiveEfficiency,
            decimal maxRangeFraction,
            decimal maxPullbackFractionOfNet,
            decimal maxSpikeConcentration,
            decimal minActiveContextHighPct,
            int contextLookbackMinutes,
            int spanToleranceMinutes,
            bool requireFlowConfirmation,
            int minTrades3h,
            decimal minImbalance3h,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(exchange) || string.IsNullOrWhiteSpace(symbol))
                return null;

            if (barMinutes <= 0) barMinutes = 5;
            if (rollingMinutes <= 0) rollingMinutes = 180;
            if (minValidBuckets <= 0) minValidBuckets = 1;
            if (maxGapMinutes <= 0) maxGapMinutes = barMinutes;
            if (maxRangeFraction <= 0m) maxRangeFraction = 0.06m;
            if (maxPullbackFractionOfNet <= 0m) maxPullbackFractionOfNet = 0.50m;
            if (maxSpikeConcentration <= 0m) maxSpikeConcentration = 0.25m;
            if (minActiveContextHighPct <= 0m) minActiveContextHighPct = 0.985m;
            if (contextLookbackMinutes <= 0) contextLookbackMinutes = 720;
            if (spanToleranceMinutes < 0) spanToleranceMinutes = 0;
            if (minTrades3h < 0) minTrades3h = 0;
            if (minWatchNetFraction < 0m) minWatchNetFraction = 0m;
            if (minWatchUpRatio < 0m) minWatchUpRatio = 0m;
            if (minActiveNetFraction < 0m) minActiveNetFraction = 0m;
            if (minActiveUpRatio < 0m) minActiveUpRatio = 0m;
            if (minActiveEfficiency < 0m) minActiveEfficiency = 0m;

            var lookbackMinutes = Math.Max(
                (rollingMinutes * 2) + 60,
                contextLookbackMinutes + rollingMinutes + 60);
            var sinceUtc = asOfUtc - TimeSpan.FromMinutes(lookbackMinutes);

            const string sql = @"
WITH cur AS (
  SELECT
    date_bin(make_interval(mins => @BarMinutes), @AsOfUtc::timestamptz, TIMESTAMPTZ '2000-01-01') AS open_bucket
),
ticks AS (
  SELECT
    date_bin(make_interval(mins => @BarMinutes), mt.utc, TIMESTAMPTZ '2000-01-01') AS t,
    AVG((mt.bid + mt.ask) / 2.0) AS mid
  FROM market_ticks mt
  CROSS JOIN cur
  WHERE mt.exchange = @Exchange
    AND mt.symbol = @Symbol
    AND mt.bid IS NOT NULL
    AND mt.ask IS NOT NULL
    AND mt.utc >= @SinceUtc
    AND date_bin(make_interval(mins => @BarMinutes), mt.utc, TIMESTAMPTZ '2000-01-01') < cur.open_bucket
  GROUP BY date_bin(make_interval(mins => @BarMinutes), mt.utc, TIMESTAMPTZ '2000-01-01')
),
trades AS (
  SELECT
    date_bin(make_interval(mins => @BarMinutes), ct.utc, TIMESTAMPTZ '2000-01-01') AS t,
    COUNT(*)::int AS trade_count,
    COALESCE(SUM(CASE WHEN ct.side='buy' THEN ct.quantity ELSE 0 END),0) AS buy_qty,
    COALESCE(SUM(CASE WHEN ct.side='sell' THEN ct.quantity ELSE 0 END),0) AS sell_qty
  FROM crypto_trades ct
  CROSS JOIN cur
  WHERE ct.exchange = @Exchange
    AND ct.symbol = @Symbol
    AND ct.utc >= @SinceUtc
    AND date_bin(make_interval(mins => @BarMinutes), ct.utc, TIMESTAMPTZ '2000-01-01') < cur.open_bucket
  GROUP BY date_bin(make_interval(mins => @BarMinutes), ct.utc, TIMESTAMPTZ '2000-01-01')
),
base AS (
  SELECT
    tk.t,
    tk.mid,
    COALESCE(tr.trade_count, 0) AS trade_count,
    COALESCE(tr.buy_qty, 0) AS buy_qty,
    COALESCE(tr.sell_qty, 0) AS sell_qty
  FROM ticks tk
  LEFT JOIN trades tr ON tr.t = tk.t
),
series AS (
  SELECT
    t,
    mid,
    trade_count,
    buy_qty,
    sell_qty,
    LAG(mid) OVER (ORDER BY t) AS prev_mid,
    (t - LAG(t) OVER (ORDER BY t)) AS gap
  FROM base
),
win AS (
  SELECT
    t,
    mid,
    COUNT(*) OVER w AS rows_3h,
    FIRST_VALUE(t) OVER w AS start_t,
    FIRST_VALUE(mid) OVER w AS start_mid,
    LAST_VALUE(mid) OVER w AS end_mid,
    MIN(mid) OVER w AS min_mid,
    MAX(mid) OVER w AS max_mid,
    AVG(CASE WHEN prev_mid IS NOT NULL AND mid > prev_mid THEN 1.0 ELSE 0.0 END) OVER w AS up_ratio_3h,
    MAX(gap) OVER w AS max_gap_3h,
    SUM(trade_count) OVER w AS trades_3h,
    SUM(buy_qty) OVER w AS buy_3h,
    SUM(sell_qty) OVER w AS sell_3h,
    SUM(CASE WHEN prev_mid IS NULL THEN 0.0 ELSE ABS(mid - prev_mid) END) OVER w AS path_3h,
    SUM(CASE WHEN prev_mid IS NOT NULL AND mid > prev_mid THEN (mid - prev_mid) ELSE 0.0 END) OVER w AS pos_delta_sum_3h,
    MAX(CASE WHEN prev_mid IS NOT NULL AND mid > prev_mid THEN (mid - prev_mid) ELSE 0.0 END) OVER w AS pos_delta_max_3h
  FROM series
  WINDOW w AS (
    ORDER BY t
    RANGE BETWEEN make_interval(mins => @RollingMinutes) PRECEDING AND CURRENT ROW
  )
),
last_row AS (
  SELECT *
  FROM win
  ORDER BY t DESC
  LIMIT 1
),
metrics AS (
  SELECT
    lr.t,
    lr.rows_3h,
    (lr.t - lr.start_t) AS span_3h,
    lr.max_gap_3h,
    lr.start_t,
    lr.end_mid,
    ((lr.end_mid - lr.start_mid) / NULLIF(lr.start_mid,0)) AS net_frac_3h,
    lr.up_ratio_3h,
    ((lr.max_mid - lr.min_mid) / NULLIF(lr.start_mid,0)) AS range_frac_3h,
    CASE WHEN lr.path_3h > 0 THEN ABS(lr.end_mid - lr.start_mid) / lr.path_3h ELSE NULL END AS eff_3h,
    CASE WHEN lr.max_mid > 0 THEN (lr.max_mid - lr.end_mid) / lr.max_mid ELSE NULL END AS pullback_frac_3h,
    lr.trades_3h,
    (lr.buy_3h - lr.sell_3h) / NULLIF(lr.buy_3h + lr.sell_3h,0) AS imb_3h,
    CASE WHEN lr.pos_delta_sum_3h > 0 THEN lr.pos_delta_max_3h / lr.pos_delta_sum_3h ELSE NULL END AS spike_3h,
    (
      SELECT MAX(b2.mid)
      FROM base b2
      WHERE b2.t >= lr.start_t - make_interval(mins => @ContextLookbackMinutes)
        AND b2.t < lr.start_t
    ) AS context_high
  FROM last_row lr
),
decision AS (
  SELECT
    @Symbol AS symbol,
    m.t AS bucket_utc,
    m.rows_3h,
    m.span_3h,
    m.max_gap_3h,
    m.net_frac_3h,
    m.up_ratio_3h,
    m.eff_3h,
    m.range_frac_3h,
    m.pullback_frac_3h,
    m.trades_3h,
    m.imb_3h,
    m.spike_3h,
    CASE WHEN m.context_high > 0 THEN m.end_mid / NULLIF(m.context_high,0) ELSE NULL END AS ctx_pct,
    (
      m.rows_3h >= @MinValidBuckets
      AND m.span_3h IS NOT NULL
      AND m.span_3h >= make_interval(mins => GREATEST(1, @RollingMinutes - @BarMinutes - @SpanToleranceMinutes))
      AND m.max_gap_3h IS NOT NULL
      AND m.max_gap_3h <= make_interval(mins => @MaxGapMinutes)
    ) AS coverage_ok,
    (
      m.net_frac_3h IS NOT NULL
      AND m.pullback_frac_3h IS NOT NULL
      AND m.net_frac_3h > 0
      AND m.pullback_frac_3h <= (@MaxPullbackFractionOfNet * m.net_frac_3h)
    ) AS no_breakdown_ok,
    (
      NOT @RequireFlowConfirmation
      OR (
        m.trades_3h >= @MinTrades3h
        AND (m.imb_3h IS NULL OR m.imb_3h >= @MinImbalance3h)
      )
    ) AS flow_ok,
    (
      m.context_high IS NULL
      OR m.context_high <= 0
      OR (m.end_mid / NULLIF(m.context_high,0)) >= @MinActiveContextHighPct
    ) AS context_ok
  FROM metrics m
)
SELECT
  d.symbol AS ""Symbol"",
  d.bucket_utc AS ""BucketUtc"",
  d.rows_3h AS ""Rows3h"",
  d.span_3h AS ""Span3h"",
  d.max_gap_3h AS ""MaxGap3h"",
  d.net_frac_3h * 10000.0 AS ""NetBps3h"",
  d.up_ratio_3h AS ""UpRatio3h"",
  d.eff_3h AS ""Eff3h"",
  d.range_frac_3h AS ""Range3hFraction"",
  d.pullback_frac_3h AS ""Pullback3hFraction"",
  d.trades_3h AS ""Trades3h"",
  d.imb_3h AS ""Imb3h"",
  d.spike_3h AS ""Spike3h"",
  d.ctx_pct AS ""CtxPct"",
  d.coverage_ok AS ""CoverageOk"",
  d.no_breakdown_ok AS ""NoBreakdownOk"",
  d.flow_ok AS ""FlowOk"",
  d.context_ok AS ""ContextOk"",
  CASE
    WHEN d.coverage_ok
     AND d.no_breakdown_ok
     AND (d.spike_3h IS NULL OR d.spike_3h <= @MaxSpikeConcentration)
     AND d.context_ok
     AND d.flow_ok
     AND d.net_frac_3h >= @MinWatchNetFraction
     AND d.up_ratio_3h >= @MinWatchUpRatio
     AND d.range_frac_3h <= @MaxRangeFraction
     AND d.net_frac_3h >= @MinActiveNetFraction
     AND d.up_ratio_3h >= @MinActiveUpRatio
     AND d.eff_3h >= @MinActiveEfficiency
    THEN TRUE ELSE FALSE
  END AS ""IsActive"",
  CASE
    WHEN NOT d.coverage_ok THEN CASE
      WHEN d.rows_3h < @MinValidBuckets THEN 'coverage'
      WHEN d.span_3h IS NULL
        OR d.span_3h < make_interval(mins => GREATEST(1, @RollingMinutes - @BarMinutes - @SpanToleranceMinutes)) THEN 'dur<window'
      ELSE 'gap'
    END
    WHEN NOT d.no_breakdown_ok THEN 'breakdown'
    WHEN d.spike_3h IS NOT NULL AND d.spike_3h > @MaxSpikeConcentration THEN 'spike'
    WHEN NOT d.context_ok THEN 'context'
    WHEN NOT d.flow_ok THEN 'flow-fade'
    WHEN d.net_frac_3h < @MinWatchNetFraction THEN 'net'
    WHEN d.up_ratio_3h < @MinWatchUpRatio THEN 'up'
    WHEN d.range_frac_3h > @MaxRangeFraction THEN 'range'
    WHEN d.net_frac_3h < @MinActiveNetFraction THEN 'activation-net'
    WHEN d.up_ratio_3h < @MinActiveUpRatio THEN 'activation-up'
    WHEN d.eff_3h < @MinActiveEfficiency THEN 'activation-eff'
    ELSE NULL
  END AS ""InactiveReason""
FROM decision d;";

            try
            {
                await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
                var row = await conn.QueryFirstOrDefaultAsync<GreenGrindDbSnapshotRow>(sql, new
                {
                    Exchange = exchange,
                    Symbol = symbol,
                    AsOfUtc = asOfUtc,
                    SinceUtc = sinceUtc,
                    BarMinutes = barMinutes,
                    RollingMinutes = rollingMinutes,
                    MinValidBuckets = minValidBuckets,
                    MaxGapMinutes = maxGapMinutes,
                    MinWatchNetFraction = minWatchNetFraction,
                    MinWatchUpRatio = minWatchUpRatio,
                    MinActiveNetFraction = minActiveNetFraction,
                    MinActiveUpRatio = minActiveUpRatio,
                    MinActiveEfficiency = minActiveEfficiency,
                    MaxRangeFraction = maxRangeFraction,
                    MaxPullbackFractionOfNet = maxPullbackFractionOfNet,
                    MaxSpikeConcentration = maxSpikeConcentration,
                    MinActiveContextHighPct = minActiveContextHighPct,
                    ContextLookbackMinutes = contextLookbackMinutes,
                    SpanToleranceMinutes = spanToleranceMinutes,
                    RequireFlowConfirmation = requireFlowConfirmation,
                    MinTrades3h = minTrades3h,
                    MinImbalance3h = minImbalance3h
                }).ConfigureAwait(false);

                return row ?? new GreenGrindDbSnapshotRow(
                    Symbol: symbol,
                    BucketUtc: asOfUtc,
                    Rows3h: 0,
                    Span3h: null,
                    MaxGap3h: null,
                    NetBps3h: null,
                    UpRatio3h: null,
                    Eff3h: null,
                    Range3hFraction: null,
                    Pullback3hFraction: null,
                    Trades3h: 0,
                    Imb3h: null,
                    Spike3h: null,
                    CtxPct: null,
                    CoverageOk: false,
                    NoBreakdownOk: false,
                    FlowOk: !requireFlowConfirmation,
                    ContextOk: false,
                    IsActive: false,
                    InactiveReason: "no-data");
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DB-TICKS] failed GreenGrind latest snapshot exchange={Exchange} symbol={Symbol}", exchange, symbol);
                return null;
            }
        }
    }

    public sealed record MarketTickMidBucketRow(string Symbol, DateTime BucketUtc, decimal Mid);
    public sealed record GreenGrindDbSnapshotRow(
        string Symbol,
        DateTime BucketUtc,
        long Rows3h,
        TimeSpan? Span3h,
        TimeSpan? MaxGap3h,
        decimal? NetBps3h,
        decimal? UpRatio3h,
        decimal? Eff3h,
        decimal? Range3hFraction,
        decimal? Pullback3hFraction,
        long Trades3h,
        decimal? Imb3h,
        decimal? Spike3h,
        decimal? CtxPct,
        bool CoverageOk,
        bool NoBreakdownOk,
        bool FlowOk,
        bool ContextOk,
        bool IsActive,
        string? InactiveReason);
}
