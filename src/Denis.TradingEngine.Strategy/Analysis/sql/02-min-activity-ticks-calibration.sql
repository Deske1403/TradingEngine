-- MinActivityTicks calibration (Bitfinex, data-driven)
-- Date: 2026-02-23
--
-- Purpose:
-- Recompute "true" activity ticks using deduped crypto_trades (by trade_id)
-- on the same decision timestamps recorded in signal_slayer_decisions.
--
-- Default assumptions:
-- - Activity window = 60s (from pullback config)
-- - Current MinActivityTicks = 2 (example; replace if changed)

WITH params AS (
    SELECT
        60::int AS activity_window_sec,
        2::int  AS current_min_activity_ticks
),
decisions AS (
    SELECT
        d.id,
        d.utc,
        COALESCE(NULLIF(d.exchange, ''), 'Bitfinex') AS exchange,
        d.symbol,
        d.accepted,
        d.reason_code,
        d.activity_ticks AS recorded_activity_ticks
    FROM signal_slayer_decisions d
    WHERE COALESCE(NULLIF(d.exchange, ''), 'Bitfinex') = 'Bitfinex'
      AND d.activity_ticks IS NOT NULL
      AND d.utc >= now() - interval '30 days'
),
recomputed AS (
    SELECT
        d.*,
        x.dedup_activity_ticks,
        CASE
            WHEN d.recorded_activity_ticks > 0
            THEN x.dedup_activity_ticks::numeric / d.recorded_activity_ticks::numeric
            ELSE NULL
        END AS dedup_to_recorded_ratio
    FROM decisions d
    CROSS JOIN params p
    LEFT JOIN LATERAL (
        SELECT COUNT(*)::int AS dedup_activity_ticks
        FROM (
            SELECT DISTINCT ON (ct.trade_id)
                ct.trade_id
            FROM crypto_trades ct
            WHERE ct.exchange = d.exchange
              AND ct.symbol = d.symbol
              AND ct.utc > d.utc - make_interval(secs => p.activity_window_sec)
              AND ct.utc <= d.utc
              AND ct.trade_id IS NOT NULL
            ORDER BY ct.trade_id, ct.utc DESC
        ) t
    ) x ON TRUE
),
augmented AS (
    SELECT
        r.*,
        CASE
            WHEN r.recorded_activity_ticks >= (SELECT current_min_activity_ticks FROM params)
            THEN TRUE ELSE FALSE
        END AS passes_current_recorded_threshold
    FROM recomputed r
)

-- --------------------------------------------------------------------
-- A) High-level summary (global + by symbol)
-- --------------------------------------------------------------------
SELECT
    'GLOBAL' AS scope,
    NULL::text AS symbol,
    COUNT(*) AS n,
    ROUND(AVG(recorded_activity_ticks)::numeric, 2) AS avg_recorded_ticks,
    ROUND(AVG(dedup_activity_ticks)::numeric, 2) AS avg_dedup_ticks,
    ROUND(AVG(dedup_to_recorded_ratio)::numeric, 4) AS avg_ratio,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY dedup_to_recorded_ratio)::numeric, 4) AS med_ratio
FROM augmented

UNION ALL

SELECT
    'BY_SYMBOL' AS scope,
    symbol,
    COUNT(*) AS n,
    ROUND(AVG(recorded_activity_ticks)::numeric, 2) AS avg_recorded_ticks,
    ROUND(AVG(dedup_activity_ticks)::numeric, 2) AS avg_dedup_ticks,
    ROUND(AVG(dedup_to_recorded_ratio)::numeric, 4) AS avg_ratio,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY dedup_to_recorded_ratio)::numeric, 4) AS med_ratio
FROM augmented
GROUP BY symbol
ORDER BY scope, symbol;

-- --------------------------------------------------------------------
-- B) Recorded-vs-dedup threshold mapping (preserve semantics)
--    Shows how many decisions that passed old recorded threshold would pass
--    each candidate dedup threshold.
-- --------------------------------------------------------------------
WITH params AS (
    SELECT 2::int AS current_min_activity_ticks
),
decisions AS (
    SELECT
        d.id,
        d.utc,
        COALESCE(NULLIF(d.exchange, ''), 'Bitfinex') AS exchange,
        d.symbol,
        d.activity_ticks AS recorded_activity_ticks
    FROM signal_slayer_decisions d
    WHERE COALESCE(NULLIF(d.exchange, ''), 'Bitfinex') = 'Bitfinex'
      AND d.activity_ticks IS NOT NULL
      AND d.utc >= now() - interval '30 days'
),
recomputed AS (
    SELECT
        d.*,
        x.dedup_activity_ticks
    FROM decisions d
    LEFT JOIN LATERAL (
        SELECT COUNT(*)::int AS dedup_activity_ticks
        FROM (
            SELECT DISTINCT ON (ct.trade_id) ct.trade_id
            FROM crypto_trades ct
            WHERE ct.exchange = d.exchange
              AND ct.symbol = d.symbol
              AND ct.utc > d.utc - interval '60 seconds'
              AND ct.utc <= d.utc
              AND ct.trade_id IS NOT NULL
            ORDER BY ct.trade_id, ct.utc DESC
        ) t
    ) x ON TRUE
),
base_pass AS (
    SELECT *
    FROM recomputed
    WHERE recorded_activity_ticks >= (SELECT current_min_activity_ticks FROM params)
),
candidates AS (
    SELECT generate_series(1, 60) AS dedup_threshold
)
SELECT
    c.dedup_threshold,
    COUNT(*) AS base_population,
    COUNT(*) FILTER (WHERE b.dedup_activity_ticks >= c.dedup_threshold) AS would_pass,
    ROUND(
        COUNT(*) FILTER (WHERE b.dedup_activity_ticks >= c.dedup_threshold)::numeric
        / NULLIF(COUNT(*), 0), 4) AS pass_rate_vs_old_passes
FROM candidates c
CROSS JOIN base_pass b
GROUP BY c.dedup_threshold
ORDER BY c.dedup_threshold;

-- --------------------------------------------------------------------
-- C) Focus on accepted decisions only (optional sanity view)
-- --------------------------------------------------------------------
WITH decisions AS (
    SELECT
        d.id,
        d.utc,
        COALESCE(NULLIF(d.exchange, ''), 'Bitfinex') AS exchange,
        d.symbol,
        d.accepted,
        d.activity_ticks AS recorded_activity_ticks
    FROM signal_slayer_decisions d
    WHERE COALESCE(NULLIF(d.exchange, ''), 'Bitfinex') = 'Bitfinex'
      AND d.activity_ticks IS NOT NULL
      AND d.utc >= now() - interval '30 days'
      AND d.accepted = TRUE
),
recomputed AS (
    SELECT
        d.*,
        x.dedup_activity_ticks
    FROM decisions d
    LEFT JOIN LATERAL (
        SELECT COUNT(*)::int AS dedup_activity_ticks
        FROM (
            SELECT DISTINCT ON (ct.trade_id) ct.trade_id
            FROM crypto_trades ct
            WHERE ct.exchange = d.exchange
              AND ct.symbol = d.symbol
              AND ct.utc > d.utc - interval '60 seconds'
              AND ct.utc <= d.utc
              AND ct.trade_id IS NOT NULL
            ORDER BY ct.trade_id, ct.utc DESC
        ) t
    ) x ON TRUE
)
SELECT
    symbol,
    COUNT(*) AS n_accepted,
    ROUND(AVG(recorded_activity_ticks)::numeric, 2) AS avg_recorded_ticks,
    ROUND(AVG(dedup_activity_ticks)::numeric, 2) AS avg_dedup_ticks,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY dedup_activity_ticks)::numeric, 2) AS med_dedup_ticks
FROM recomputed
GROUP BY symbol
ORDER BY symbol;

