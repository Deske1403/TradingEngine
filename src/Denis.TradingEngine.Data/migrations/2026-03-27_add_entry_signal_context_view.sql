BEGIN;

CREATE OR REPLACE VIEW v_entry_signal_context AS
SELECT
    s.utc               AS signal_utc,
    s.symbol,
    s.side              AS signal_side,
    s.strategy,
    s.reason            AS signal_reason,
    s.accepted,
    s.reject_reason,
    s.correlation_id,
    s.run_env,
    s.rth_window,
    s.trading_phase,
    s.suggested_price,
    s.planned_qty,
    s.planned_notional,
    j.utc               AS entry_fill_utc,
    j.side              AS entry_side,
    j.price             AS entry_fill_price,
    j.quantity          AS entry_fill_qty,
    j.notional          AS entry_fill_notional,
    j.realized_pnl,
    j.is_paper,
    EXTRACT(EPOCH FROM (j.utc - s.utc))::NUMERIC(18,3) AS signal_to_fill_seconds
FROM trade_signals s
JOIN trade_journal j
  ON j.correlation_id = s.correlation_id
WHERE s.accepted = TRUE
  AND COALESCE(j.is_exit, FALSE) = FALSE
ORDER BY s.utc DESC;

COMMIT;
