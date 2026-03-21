-- IBKR trend calibration queries for market_ticks
-- Purpose: tune TrendAnalysisSettings using real data instead of guesswork.
--
-- Mapping to appsettings.json (Trading section):
-- - TrendUseExplicitRangeMins       -> lookback_minutes
-- - TrendMinPoints                  -> min_points
-- - TrendNeutralThresholdFraction   -> neutral_threshold
-- - TrendEndpointWeight             -> endpoint_w
-- - TrendSlopeWeight                -> slope_w
-- - TrendDrawdownPenaltyWeight      -> dd_penalty_w
-- - TrendMaxDrawdownClampFraction   -> dd_clamp

-- Default analysis window for this file: 2 days
-- (change window_days in each cfg/params CTE below if needed)
-- ============================================================
-- 0) Sanity: do we have IBKR ticks in this DB?
-- ============================================================
SELECT
    exchange,
    COUNT(*) AS ticks,
    MIN(utc) AS first_utc,
    MAX(utc) AS last_utc
FROM market_ticks
GROUP BY exchange
ORDER BY ticks DESC;

-- ============================================================
-- 1) Per-symbol quality stats for IBKR ticks
-- ============================================================
WITH cfg AS (
    SELECT
        'SMART'::text AS exchange_filter,
        now() - make_interval(days => 2) AS from_utc,
        now() AS to_utc
)
SELECT
    mt.symbol,
    COUNT(*) AS tick_count,
    MIN(mt.utc) AS first_utc,
    MAX(mt.utc) AS last_utc,
    ROUND(AVG(((mt.ask - mt.bid) / NULLIF((mt.ask + mt.bid) / 2, 0)) * 10000)::numeric, 3) AS avg_spread_bps,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ((mt.ask - mt.bid) / NULLIF((mt.ask + mt.bid) / 2, 0)) * 10000)::numeric, 3) AS p50_spread_bps,
    ROUND(PERCENTILE_CONT(0.9) WITHIN GROUP (ORDER BY ((mt.ask - mt.bid) / NULLIF((mt.ask + mt.bid) / 2, 0)) * 10000)::numeric, 3) AS p90_spread_bps
FROM market_ticks mt
CROSS JOIN cfg
WHERE mt.exchange = cfg.exchange_filter
  AND mt.utc >= cfg.from_utc
  AND mt.utc < cfg.to_utc
GROUP BY mt.symbol
ORDER BY tick_count DESC;

-- ============================================================
-- 2) Trend score components at BUY signal timestamps
--    Uses same math as TrendPriceMath:
--    score = endpoint_w*endpoint_return + slope_w*slope_return - dd_penalty_w*clamped_dd
-- ============================================================
WITH params AS (
    SELECT
        'SMART'::text AS exchange_filter,
        120::int AS lookback_minutes,
        30::int AS min_points,
        0.0003::numeric AS neutral_threshold,
        0.55::numeric AS endpoint_w,
        0.45::numeric AS slope_w,
        0.15::numeric AS dd_penalty_w,
        0.05::numeric AS dd_clamp,
        now() - make_interval(days => 2) AS from_utc,
        now() AS to_utc
),
signals AS (
    SELECT
        s.id AS signal_id,
        s.utc AS signal_utc,
        s.symbol,
        s.accepted,
        s.reject_reason,
        s.exchange
    FROM trade_signals s
    CROSS JOIN params p
    WHERE s.utc >= p.from_utc
      AND s.utc < p.to_utc
      AND upper(s.side) = 'BUY'
      AND COALESCE(s.exchange, 'SMART') IN ('SMART', 'IBKR')
),
components AS (
    SELECT
        s.signal_id,
        s.signal_utc,
        s.symbol,
        s.accepted,
        s.reject_reason,
        c.n_points,
        c.first_price,
        c.last_price,
        c.slope_per_step,
        c.max_drawdown
    FROM signals s
    CROSS JOIN params p
    CROSS JOIN LATERAL (
        WITH w AS (
            SELECT
                mt.utc,
                COALESCE((mt.bid + mt.ask) / 2, mt.bid, mt.ask) AS price
            FROM market_ticks mt
            WHERE mt.exchange = p.exchange_filter
              AND mt.symbol = s.symbol
              AND mt.utc > s.signal_utc - make_interval(mins => p.lookback_minutes)
              AND mt.utc <= s.signal_utc
              AND COALESCE((mt.bid + mt.ask) / 2, mt.bid, mt.ask) > 0
            ORDER BY mt.utc
        ),
        idx AS (
            SELECT
                utc,
                price,
                ROW_NUMBER() OVER (ORDER BY utc) - 1 AS x
            FROM w
        ),
        dd AS (
            SELECT
                price,
                MAX(price) OVER (ORDER BY x ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS run_peak
            FROM idx
        )
        SELECT
            (SELECT COUNT(*) FROM w) AS n_points,
            (SELECT price FROM w ORDER BY utc ASC LIMIT 1) AS first_price,
            (SELECT price FROM w ORDER BY utc DESC LIMIT 1) AS last_price,
            COALESCE((SELECT regr_slope(price::double precision, x::double precision) FROM idx), 0)::numeric AS slope_per_step,
            COALESCE((SELECT MAX((run_peak - price) / NULLIF(run_peak, 0)) FROM dd), 0)::numeric AS max_drawdown
    ) c
),
scored AS (
    SELECT
        c.signal_id,
        c.signal_utc,
        c.symbol,
        c.accepted,
        c.reject_reason,
        c.n_points,
        c.first_price,
        c.last_price,
        ((c.last_price - c.first_price) / NULLIF(c.first_price, 0))::numeric AS endpoint_return,
        ((c.slope_per_step * GREATEST(c.n_points - 1, 0)) / NULLIF(c.first_price, 0))::numeric AS slope_return,
        LEAST(c.max_drawdown, p.dd_clamp)::numeric AS clamped_dd,
        (
            (p.endpoint_w * ((c.last_price - c.first_price) / NULLIF(c.first_price, 0))) +
            (p.slope_w * ((c.slope_per_step * GREATEST(c.n_points - 1, 0)) / NULLIF(c.first_price, 0))) -
            (p.dd_penalty_w * LEAST(c.max_drawdown, p.dd_clamp))
        )::numeric AS score,
        p.neutral_threshold
    FROM components c
    CROSS JOIN params p
    WHERE c.n_points >= p.min_points
      AND c.first_price > 0
)
SELECT
    signal_utc,
    symbol,
    accepted,
    reject_reason,
    n_points,
    ROUND(endpoint_return, 8) AS endpoint_return,
    ROUND(slope_return, 8) AS slope_return,
    ROUND(clamped_dd, 8) AS clamped_dd,
    ROUND(score, 8) AS score,
    CASE
        WHEN score > neutral_threshold THEN 'Up'
        WHEN score < -neutral_threshold THEN 'Down'
        ELSE 'Neutral'
    END AS trend_direction
FROM scored
ORDER BY signal_utc DESC
LIMIT 500;

-- ============================================================
-- 3) Summary for threshold tuning (same scored CTE as above)
--    Copy-paste the CTE block from query #2 and run this SELECT.
-- ============================================================
-- SELECT
--     COUNT(*) AS signals_total,
--     COUNT(*) FILTER (WHERE score < -neutral_threshold) AS down_count,
--     COUNT(*) FILTER (WHERE ABS(score) <= neutral_threshold) AS neutral_count,
--     COUNT(*) FILTER (WHERE score > neutral_threshold) AS up_count,
--     ROUND(PERCENTILE_CONT(0.1) WITHIN GROUP (ORDER BY score)::numeric, 8) AS p10_score,
--     ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY score)::numeric, 8) AS p50_score,
--     ROUND(PERCENTILE_CONT(0.9) WITHIN GROUP (ORDER BY score)::numeric, 8) AS p90_score,
--     ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ABS(score))::numeric, 8) AS p50_abs_score,
--     ROUND(PERCENTILE_CONT(0.8) WITHIN GROUP (ORDER BY ABS(score))::numeric, 8) AS p80_abs_score
-- FROM scored;
