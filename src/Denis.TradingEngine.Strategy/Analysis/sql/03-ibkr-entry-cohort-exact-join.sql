-- Weekly IBKR entry review with exact signal -> fill linkage.
-- Update the params CTE before running.

WITH params AS (
    SELECT
        TIMESTAMPTZ '2026-03-23 00:00:00+00' AS from_utc,
        TIMESTAMPTZ '2026-03-30 00:00:00+00' AS to_utc
),
entries AS (
    SELECT
        v.signal_utc,
        v.entry_fill_utc,
        v.signal_to_fill_seconds,
        v.symbol,
        v.signal_side,
        v.strategy,
        v.signal_reason,
        v.correlation_id,
        v.suggested_price,
        v.planned_qty,
        v.planned_notional,
        v.entry_fill_price,
        v.entry_fill_qty,
        v.entry_fill_notional,
        v.run_env,
        v.rth_window,
        v.trading_phase
    FROM v_entry_signal_context v
    CROSS JOIN params p
    WHERE v.signal_utc >= p.from_utc
      AND v.signal_utc < p.to_utc
      AND v.rth_window <> 'n/a'
)
SELECT *
FROM entries
ORDER BY signal_utc, symbol;
