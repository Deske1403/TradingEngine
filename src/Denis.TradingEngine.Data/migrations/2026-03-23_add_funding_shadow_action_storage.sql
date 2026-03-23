BEGIN;

CREATE TABLE IF NOT EXISTS funding_shadow_actions (
    id                    BIGSERIAL       PRIMARY KEY,
    utc                   TIMESTAMPTZ     NOT NULL,
    exchange              TEXT            NOT NULL,
    action_key            TEXT            NOT NULL,
    plan_key              TEXT            NOT NULL,
    symbol                TEXT            NOT NULL,
    currency              TEXT            NOT NULL,
    regime                TEXT            NOT NULL,
    bucket                TEXT            NOT NULL,
    action                TEXT            NOT NULL,
    is_actionable         BOOLEAN         NOT NULL,
    available_balance     NUMERIC(18,8)   NOT NULL,
    lendable_balance      NUMERIC(18,8)   NOT NULL,
    allocation_amount     NUMERIC(18,8)   NOT NULL DEFAULT 0,
    allocation_fraction   NUMERIC(18,12)  NOT NULL DEFAULT 0,
    target_rate           NUMERIC(18,12),
    fallback_rate         NUMERIC(18,12),
    target_period_days    INT,
    max_wait_minutes      INT,
    decision_deadline_utc TIMESTAMPTZ,
    role                  TEXT,
    fallback_bucket       TEXT,
    active_offer_count    INT             NOT NULL DEFAULT 0,
    active_offer_id       BIGINT,
    active_offer_rate     NUMERIC(18,12),
    active_offer_amount   NUMERIC(18,8),
    active_offer_status   TEXT,
    reason                TEXT            NOT NULL,
    summary               TEXT,
    metadata              JSONB,

    CONSTRAINT ux_funding_shadow_actions_exchange_key UNIQUE (exchange, action_key)
);

CREATE INDEX IF NOT EXISTS ix_funding_shadow_actions_exchange_symbol_utc
    ON funding_shadow_actions (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_shadow_actions_action_utc
    ON funding_shadow_actions (action, utc DESC);

DROP VIEW IF EXISTS v_funding_shadow_action_vs_actual;

CREATE VIEW v_funding_shadow_action_vs_actual AS
WITH latest_shadow_action AS (
    SELECT
        sa.*,
        ROW_NUMBER() OVER (PARTITION BY sa.exchange, sa.symbol, sa.bucket ORDER BY sa.utc DESC, sa.id DESC) AS rn
    FROM funding_shadow_actions sa
),
shadow_action_rollup AS (
    SELECT
        exchange,
        symbol,
        MAX(utc) AS latest_shadow_action_utc,
        MAX(regime) AS latest_regime,
        MAX(summary) AS latest_summary,
        BOOL_OR(is_actionable) AS has_actionable_shadow_action,
        MAX(CASE WHEN bucket = 'Motor' THEN action END) AS motor_action,
        MAX(CASE WHEN bucket = 'Motor' THEN target_rate END) AS motor_target_rate,
        MAX(CASE WHEN bucket = 'Motor' THEN fallback_rate END) AS motor_fallback_rate,
        MAX(CASE WHEN bucket = 'Motor' THEN decision_deadline_utc END) AS motor_deadline_utc,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN action END) AS opportunistic_action,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN target_rate END) AS opportunistic_target_rate,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN fallback_rate END) AS opportunistic_fallback_rate,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN decision_deadline_utc END) AS opportunistic_deadline_utc
    FROM latest_shadow_action
    WHERE rn = 1
    GROUP BY exchange, symbol
),
latest_live_action AS (
    SELECT
        foa.*,
        ROW_NUMBER() OVER (PARTITION BY foa.exchange, foa.symbol ORDER BY foa.utc DESC, foa.id DESC) AS rn
    FROM funding_offer_actions foa
),
actual_latest AS (
    SELECT
        exchange,
        symbol,
        utc AS last_live_action_utc,
        action AS last_live_action,
        amount AS last_live_amount,
        rate AS last_live_rate,
        reason AS last_live_reason
    FROM latest_live_action
    WHERE rn = 1
),
book_rollup AS (
    SELECT
        exchange,
        symbol,
        COUNT(*) FILTER (WHERE status ILIKE 'CLOSED%') AS closed_cycles,
        COUNT(*) FILTER (WHERE status NOT ILIKE 'CLOSED%') AS active_cycles,
        SUM(total_net_interest) AS total_net_interest,
        MAX(last_payment_utc) AS last_payment_utc,
        MAX(principal_returned_utc) AS last_principal_returned_utc
    FROM (
        SELECT
            exchange,
            symbol,
            net_interest AS total_net_interest,
            last_payment_utc,
            principal_returned_utc,
            status
        FROM v_funding_book
    ) book_base
    GROUP BY exchange, symbol
)
SELECT
    s.exchange,
    s.symbol,
    s.latest_shadow_action_utc,
    s.latest_regime,
    s.has_actionable_shadow_action,
    s.motor_action,
    s.motor_target_rate,
    s.motor_fallback_rate,
    s.motor_deadline_utc,
    s.opportunistic_action,
    s.opportunistic_target_rate,
    s.opportunistic_fallback_rate,
    s.opportunistic_deadline_utc,
    a.last_live_action_utc,
    a.last_live_action,
    a.last_live_amount,
    a.last_live_rate,
    a.last_live_reason,
    b.closed_cycles,
    b.active_cycles,
    b.total_net_interest,
    b.last_payment_utc,
    b.last_principal_returned_utc,
    s.latest_summary
FROM shadow_action_rollup s
LEFT JOIN actual_latest a
    ON a.exchange = s.exchange
   AND a.symbol = s.symbol
LEFT JOIN book_rollup b
    ON b.exchange = s.exchange
   AND b.symbol = s.symbol;

COMMENT ON TABLE funding_shadow_actions IS 'Shadow-only next-step action policy for each funding bucket, kept separate from live offer execution';
COMMENT ON VIEW v_funding_shadow_action_vs_actual IS 'Latest shadow next-step action policy per symbol compared with latest live offer behavior and funding-book outcomes';

COMMIT;
