BEGIN;

CREATE TABLE IF NOT EXISTS funding_shadow_action_sessions (
    id                    BIGSERIAL       PRIMARY KEY,
    exchange              TEXT            NOT NULL,
    session_key           TEXT            NOT NULL,
    symbol                TEXT            NOT NULL,
    currency              TEXT            NOT NULL,
    bucket                TEXT            NOT NULL,
    first_regime          TEXT            NOT NULL,
    current_regime        TEXT            NOT NULL,
    first_action          TEXT            NOT NULL,
    current_action        TEXT            NOT NULL,
    status                TEXT            NOT NULL,
    is_actionable         BOOLEAN         NOT NULL,
    available_balance     NUMERIC(18,8)   NOT NULL,
    lendable_balance      NUMERIC(18,8)   NOT NULL,
    allocation_amount     NUMERIC(18,8)   NOT NULL DEFAULT 0,
    allocation_fraction   NUMERIC(18,12)  NOT NULL DEFAULT 0,
    target_rate_initial   NUMERIC(18,12),
    target_rate_current   NUMERIC(18,12),
    fallback_rate         NUMERIC(18,12),
    target_period_days    INT,
    max_wait_minutes      INT,
    opened_utc            TIMESTAMPTZ     NOT NULL,
    last_updated_utc      TIMESTAMPTZ     NOT NULL,
    decision_deadline_utc TIMESTAMPTZ,
    closed_utc            TIMESTAMPTZ,
    active_offer_id       BIGINT,
    active_offer_rate     NUMERIC(18,12),
    active_offer_amount   NUMERIC(18,8),
    active_offer_status   TEXT,
    resolution            TEXT,
    update_count          INT             NOT NULL DEFAULT 1,
    summary               TEXT            NOT NULL,
    metadata              JSONB,

    CONSTRAINT ux_funding_shadow_action_sessions_exchange_key UNIQUE (exchange, session_key)
);

CREATE INDEX IF NOT EXISTS ix_funding_shadow_action_sessions_exchange_symbol_opened
    ON funding_shadow_action_sessions (exchange, symbol, opened_utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_shadow_action_sessions_status_opened
    ON funding_shadow_action_sessions (status, opened_utc DESC);

DROP VIEW IF EXISTS v_funding_shadow_session_vs_actual;

CREATE VIEW v_funding_shadow_session_vs_actual AS
WITH latest_session AS (
    SELECT
        ss.*,
        ROW_NUMBER() OVER (PARTITION BY ss.exchange, ss.symbol, ss.bucket ORDER BY ss.opened_utc DESC, ss.id DESC) AS rn
    FROM funding_shadow_action_sessions ss
),
session_rollup AS (
    SELECT
        exchange,
        symbol,
        MAX(opened_utc) AS latest_session_opened_utc,
        MAX(last_updated_utc) AS latest_session_updated_utc,
        MAX(closed_utc) AS latest_session_closed_utc,
        BOOL_OR(closed_utc IS NULL) AS has_open_session,
        MAX(CASE WHEN bucket = 'Motor' THEN status END) AS motor_status,
        MAX(CASE WHEN bucket = 'Motor' THEN current_action END) AS motor_current_action,
        MAX(CASE WHEN bucket = 'Motor' THEN resolution END) AS motor_resolution,
        MAX(CASE WHEN bucket = 'Motor' THEN target_rate_current END) AS motor_target_rate_current,
        MAX(CASE WHEN bucket = 'Motor' THEN decision_deadline_utc END) AS motor_deadline_utc,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN status END) AS opportunistic_status,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN current_action END) AS opportunistic_current_action,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN resolution END) AS opportunistic_resolution,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN target_rate_current END) AS opportunistic_target_rate_current,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN decision_deadline_utc END) AS opportunistic_deadline_utc
    FROM latest_session
    WHERE rn = 1
    GROUP BY exchange, symbol
),
actual_latest AS (
    SELECT
        exchange,
        symbol,
        MAX(utc) AS last_live_action_utc,
        MAX(action) AS last_live_action
    FROM funding_offer_actions
    GROUP BY exchange, symbol
),
book_rollup AS (
    SELECT
        exchange,
        symbol,
        COUNT(*) FILTER (WHERE status ILIKE 'CLOSED%') AS closed_cycles,
        COUNT(*) FILTER (WHERE status NOT ILIKE 'CLOSED%') AS active_cycles,
        SUM(net_interest) AS total_net_interest,
        MAX(last_payment_utc) AS last_payment_utc,
        MAX(principal_returned_utc) AS last_principal_returned_utc
    FROM v_funding_book
    GROUP BY exchange, symbol
)
SELECT
    s.exchange,
    s.symbol,
    s.latest_session_opened_utc,
    s.latest_session_updated_utc,
    s.latest_session_closed_utc,
    s.has_open_session,
    s.motor_status,
    s.motor_current_action,
    s.motor_resolution,
    s.motor_target_rate_current,
    s.motor_deadline_utc,
    s.opportunistic_status,
    s.opportunistic_current_action,
    s.opportunistic_resolution,
    s.opportunistic_target_rate_current,
    s.opportunistic_deadline_utc,
    a.last_live_action_utc,
    a.last_live_action,
    b.closed_cycles,
    b.active_cycles,
    b.total_net_interest,
    b.last_payment_utc,
    b.last_principal_returned_utc
FROM session_rollup s
LEFT JOIN actual_latest a
    ON a.exchange = s.exchange
   AND a.symbol = s.symbol
LEFT JOIN book_rollup b
    ON b.exchange = s.exchange
   AND b.symbol = s.symbol;

COMMENT ON TABLE funding_shadow_action_sessions IS 'Stateful shadow-action lifecycles that track wait/place/fallback sessions across consecutive funding cycles';
COMMENT ON VIEW v_funding_shadow_session_vs_actual IS 'Latest shadow action session per symbol compared with live offer behavior and realized funding-book outcomes';

COMMIT;
