BEGIN;

CREATE TABLE IF NOT EXISTS funding_shadow_plans (
    id                  BIGSERIAL       PRIMARY KEY,
    utc                 TIMESTAMPTZ     NOT NULL,
    exchange            TEXT            NOT NULL,
    plan_key            TEXT            NOT NULL,
    symbol              TEXT            NOT NULL,
    currency            TEXT            NOT NULL,
    regime              TEXT            NOT NULL,
    bucket              TEXT            NOT NULL,
    available_balance   NUMERIC(18,8)   NOT NULL,
    lendable_balance    NUMERIC(18,8)   NOT NULL,
    allocation_amount   NUMERIC(18,8)   NOT NULL DEFAULT 0,
    allocation_fraction NUMERIC(18,12)  NOT NULL DEFAULT 0,
    target_rate         NUMERIC(18,12),
    target_period_days  INT,
    max_wait_minutes    INT,
    role                TEXT,
    fallback_bucket     TEXT,
    market_ask_rate     NUMERIC(18,12),
    market_bid_rate     NUMERIC(18,12),
    summary             TEXT,
    metadata            JSONB,

    CONSTRAINT ux_funding_shadow_plans_exchange_key UNIQUE (exchange, plan_key)
);

CREATE INDEX IF NOT EXISTS ix_funding_shadow_plans_exchange_symbol_utc
    ON funding_shadow_plans (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_shadow_plans_bucket_utc
    ON funding_shadow_plans (bucket, utc DESC);

CREATE OR REPLACE VIEW v_funding_shadow_vs_actual AS
WITH latest_shadow AS (
    SELECT
        sp.*,
        ROW_NUMBER() OVER (PARTITION BY sp.exchange, sp.symbol, sp.bucket ORDER BY sp.utc DESC, sp.id DESC) AS rn
    FROM funding_shadow_plans sp
),
shadow_rollup AS (
    SELECT
        exchange,
        symbol,
        MAX(utc) AS latest_plan_utc,
        MAX(regime) AS latest_regime,
        MAX(available_balance) AS available_balance,
        MAX(lendable_balance) AS lendable_balance,
        MAX(summary) AS latest_summary,
        MAX(CASE WHEN bucket = 'Motor' THEN allocation_amount END) AS motor_amount,
        MAX(CASE WHEN bucket = 'Motor' THEN target_rate END) AS motor_rate,
        MAX(CASE WHEN bucket = 'Motor' THEN max_wait_minutes END) AS motor_max_wait_minutes,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN allocation_amount END) AS opportunistic_amount,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN target_rate END) AS opportunistic_rate,
        MAX(CASE WHEN bucket = 'Opportunistic' THEN max_wait_minutes END) AS opportunistic_max_wait_minutes,
        BOOL_OR(bucket <> 'NONE' AND allocation_amount > 0) AS has_actionable_plan
    FROM latest_shadow
    WHERE rn = 1
    GROUP BY exchange, symbol
),
actual_rollup AS (
    SELECT
        exchange,
        symbol,
        COUNT(*) FILTER (WHERE status ILIKE 'CLOSED%') AS closed_cycles,
        COUNT(*) FILTER (WHERE status NOT ILIKE 'CLOSED%') AS active_cycles,
        SUM(principal_returned) AS total_principal_returned,
        SUM(net_interest) AS total_net_interest,
        AVG(NULLIF(net_interest, 0)) FILTER (WHERE status ILIKE 'CLOSED%') AS avg_net_interest_closed_cycle,
        MAX(principal_returned_utc) AS last_principal_returned_utc,
        MAX(last_payment_utc) AS last_payment_utc
    FROM v_funding_book
    GROUP BY exchange, symbol
)
SELECT
    s.exchange,
    s.symbol,
    s.latest_plan_utc,
    s.latest_regime,
    s.available_balance,
    s.lendable_balance,
    s.has_actionable_plan,
    s.motor_amount,
    s.motor_rate,
    s.motor_max_wait_minutes,
    s.opportunistic_amount,
    s.opportunistic_rate,
    s.opportunistic_max_wait_minutes,
    a.closed_cycles,
    a.active_cycles,
    a.total_principal_returned,
    a.total_net_interest,
    a.avg_net_interest_closed_cycle,
    a.last_principal_returned_utc,
    a.last_payment_utc,
    s.latest_summary
FROM shadow_rollup s
LEFT JOIN actual_rollup a
    ON a.exchange = s.exchange
   AND a.symbol = s.symbol;

COMMENT ON TABLE funding_shadow_plans IS 'Shadow-only funding engine plans used to compare Motor/Opportunistic ideas against actual funding outcomes';
COMMENT ON VIEW v_funding_shadow_vs_actual IS 'Latest shadow funding plan per symbol compared with realized funding-book outcomes';

COMMIT;
