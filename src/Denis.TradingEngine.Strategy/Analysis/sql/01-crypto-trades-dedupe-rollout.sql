-- Crypto trades dedupe rollout (Bitfinex)
-- Date: 2026-02-23
--
-- Goal:
-- 1) Do NOT clean historical duplicates now
-- 2) Enforce uniqueness for NEW rows only (post-deploy cutoff)
-- 3) Pair with repository-side ON CONFLICT DO NOTHING
--
-- IMPORTANT:
-- - Run CREATE INDEX CONCURRENTLY outside a transaction.
-- - Replace the cutoff timestamp below with the actual deploy time.

-- --------------------------------------------------------------------
-- 0) Pre-check: inspect current duplicate shape (should already show dupes)
-- --------------------------------------------------------------------
WITH g AS (
    SELECT exchange, symbol, trade_id, COUNT(*) AS c
    FROM crypto_trades
    WHERE exchange = 'Bitfinex'
      AND trade_id IS NOT NULL
    GROUP BY 1,2,3
)
SELECT c AS dup_count, COUNT(*) AS groups
FROM g
GROUP BY c
ORDER BY c;

-- --------------------------------------------------------------------
-- 1) Deploy-cutoff partial unique index for NEW rows only
-- --------------------------------------------------------------------
-- Replace timestamp with actual deploy moment (UTC or TZ-aware literal).
-- Example:
--   2026-02-24 10:15:00+01
--
-- CREATE UNIQUE INDEX CONCURRENTLY ux_crypto_trades_bfx_sym_tradeid_notnull_from_cutoff
--     ON crypto_trades (exchange, symbol, trade_id)
--     WHERE trade_id IS NOT NULL
--       AND exchange = 'Bitfinex'
--       AND utc >= TIMESTAMPTZ '2026-02-24 10:15:00+01';

-- --------------------------------------------------------------------
-- 2) Post-check: duplicates AFTER cutoff must be zero
-- --------------------------------------------------------------------
-- Replace cutoff to match the deployed index predicate.
--
-- WITH g AS (
--     SELECT exchange, symbol, trade_id, COUNT(*) AS c
--     FROM crypto_trades
--     WHERE exchange = 'Bitfinex'
--       AND trade_id IS NOT NULL
--       AND utc >= TIMESTAMPTZ '2026-02-24 10:15:00+01'
--     GROUP BY 1,2,3
-- )
-- SELECT COUNT(*) AS duplicate_groups_after_cutoff
-- FROM g
-- WHERE c > 1;

-- --------------------------------------------------------------------
-- 3) Optional monitoring query (run during first 24-48h after deploy)
-- --------------------------------------------------------------------
-- WITH t AS (
--     SELECT date_trunc('hour', utc) AS h, COUNT(*) AS rows, COUNT(DISTINCT trade_id) AS distinct_trade_ids
--     FROM crypto_trades
--     WHERE exchange = 'Bitfinex'
--       AND trade_id IS NOT NULL
--       AND utc >= now() - interval '48 hours'
--     GROUP BY 1
-- )
-- SELECT h, rows, distinct_trade_ids, (rows - distinct_trade_ids) AS dup_delta
-- FROM t
-- ORDER BY h DESC;

