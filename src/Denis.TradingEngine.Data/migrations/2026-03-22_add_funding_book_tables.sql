BEGIN;

CREATE TABLE IF NOT EXISTS funding_interest_allocations (
    id                        BIGSERIAL       PRIMARY KEY,
    utc                       TIMESTAMPTZ     NOT NULL,
    exchange                  TEXT            NOT NULL,
    allocation_key            TEXT            NOT NULL,
    ledger_id                 BIGINT          NOT NULL,
    currency                  TEXT            NOT NULL,
    symbol                    TEXT,
    credit_id                 BIGINT,
    loan_id                   BIGINT,
    funding_trade_id          BIGINT,
    allocated_gross_interest  NUMERIC(18,12)  NOT NULL DEFAULT 0,
    allocated_fee_amount      NUMERIC(18,12)  NOT NULL DEFAULT 0,
    allocated_net_interest    NUMERIC(18,12)  NOT NULL DEFAULT 0,
    allocation_fraction       NUMERIC(18,12)  NOT NULL,
    allocation_method         TEXT            NOT NULL,
    confidence                TEXT,
    metadata                  JSONB,

    CONSTRAINT ux_funding_interest_allocations_exchange_key UNIQUE (exchange, allocation_key)
);

CREATE INDEX IF NOT EXISTS ix_funding_interest_allocations_exchange_symbol_utc
    ON funding_interest_allocations (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_interest_allocations_ledger_id
    ON funding_interest_allocations (ledger_id);

CREATE TABLE IF NOT EXISTS funding_capital_events (
    id                BIGSERIAL       PRIMARY KEY,
    utc               TIMESTAMPTZ     NOT NULL,
    exchange          TEXT            NOT NULL,
    event_key         TEXT            NOT NULL,
    symbol            TEXT,
    currency          TEXT,
    wallet_type       TEXT,
    event_type        TEXT            NOT NULL,
    credit_id         BIGINT,
    loan_id           BIGINT,
    funding_trade_id  BIGINT,
    amount            NUMERIC(18,12)  NOT NULL,
    source_type       TEXT            NOT NULL,
    description       TEXT,
    metadata          JSONB,

    CONSTRAINT ux_funding_capital_events_exchange_key UNIQUE (exchange, event_key)
);

CREATE INDEX IF NOT EXISTS ix_funding_capital_events_exchange_symbol_utc
    ON funding_capital_events (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_capital_events_type_utc
    ON funding_capital_events (event_type, utc DESC);

CREATE OR REPLACE VIEW v_funding_book AS
WITH lifecycles AS (
    SELECT
        'credit'::TEXT AS lifecycle_type,
        exchange,
        symbol,
        credit_id AS lifecycle_id,
        credit_id,
        NULL::BIGINT AS loan_id,
        status,
        CASE
            WHEN original_amount IS NULL OR ABS(original_amount) < 0.00000001 THEN amount
            ELSE original_amount
        END AS principal_amount,
        amount AS current_amount,
        rate,
        period_days,
        created_utc,
        updated_utc,
        opened_utc,
        closed_utc
    FROM funding_credits

    UNION ALL

    SELECT
        'loan'::TEXT AS lifecycle_type,
        exchange,
        symbol,
        loan_id AS lifecycle_id,
        NULL::BIGINT AS credit_id,
        loan_id,
        status,
        CASE
            WHEN original_amount IS NULL OR ABS(original_amount) < 0.00000001 THEN amount
            ELSE original_amount
        END AS principal_amount,
        amount AS current_amount,
        rate,
        period_days,
        created_utc,
        updated_utc,
        opened_utc,
        closed_utc
    FROM funding_loans
),
interest_rollup AS (
    SELECT
        exchange,
        symbol,
        credit_id,
        loan_id,
        SUM(allocated_gross_interest) AS gross_interest,
        SUM(allocated_fee_amount) AS fee_amount,
        SUM(allocated_net_interest) AS net_interest,
        COUNT(*) AS payment_count,
        MAX(utc) AS last_payment_utc
    FROM funding_interest_allocations
    GROUP BY exchange, symbol, credit_id, loan_id
),
capital_rollup AS (
    SELECT
        exchange,
        symbol,
        credit_id,
        loan_id,
        MAX(CASE WHEN event_type = 'principal_deployed' THEN utc END) AS principal_deployed_utc,
        SUM(CASE WHEN event_type = 'principal_deployed' THEN amount ELSE 0 END) AS principal_deployed,
        MAX(CASE WHEN event_type = 'principal_returned' THEN utc END) AS principal_returned_utc,
        SUM(CASE WHEN event_type = 'principal_returned' THEN amount ELSE 0 END) AS principal_returned,
        SUM(CASE WHEN event_type = 'interest_paid' THEN amount ELSE 0 END) AS net_interest_from_events
    FROM funding_capital_events
    GROUP BY exchange, symbol, credit_id, loan_id
),
trade_rollup AS (
    SELECT
        exchange,
        symbol,
        credit_id,
        loan_id,
        COUNT(*) AS trade_count,
        MIN(utc) AS first_trade_utc,
        MAX(utc) AS last_trade_utc
    FROM funding_trades
    GROUP BY exchange, symbol, credit_id, loan_id
)
SELECT
    l.lifecycle_type,
    l.exchange,
    l.symbol,
    l.lifecycle_id,
    l.credit_id,
    l.loan_id,
    l.status,
    l.principal_amount,
    l.current_amount,
    l.rate,
    l.period_days,
    l.created_utc,
    l.updated_utc,
    l.opened_utc,
    l.closed_utc,
    c.principal_deployed_utc,
    c.principal_deployed,
    c.principal_returned_utc,
    c.principal_returned,
    COALESCE(i.gross_interest, 0) AS gross_interest,
    COALESCE(i.fee_amount, 0) AS fee_amount,
    COALESCE(i.net_interest, 0) AS net_interest,
    COALESCE(c.net_interest_from_events, 0) AS net_interest_from_events,
    COALESCE(i.payment_count, 0) AS payment_count,
    i.last_payment_utc,
    COALESCE(t.trade_count, 0) AS trade_count,
    t.first_trade_utc,
    t.last_trade_utc
FROM lifecycles l
LEFT JOIN interest_rollup i
    ON i.exchange = l.exchange
   AND i.symbol IS NOT DISTINCT FROM l.symbol
   AND i.credit_id IS NOT DISTINCT FROM l.credit_id
   AND i.loan_id IS NOT DISTINCT FROM l.loan_id
LEFT JOIN capital_rollup c
    ON c.exchange = l.exchange
   AND c.symbol IS NOT DISTINCT FROM l.symbol
   AND c.credit_id IS NOT DISTINCT FROM l.credit_id
   AND c.loan_id IS NOT DISTINCT FROM l.loan_id
LEFT JOIN trade_rollup t
    ON t.exchange = l.exchange
   AND t.symbol IS NOT DISTINCT FROM l.symbol
   AND t.credit_id IS NOT DISTINCT FROM l.credit_id
   AND t.loan_id IS NOT DISTINCT FROM l.loan_id;

COMMENT ON TABLE funding_interest_allocations IS 'Normalized allocation of raw funding payments across credit/loan lifecycles';
COMMENT ON TABLE funding_capital_events IS 'Business-level funding book events: principal deployed, returned, and interest paid';
COMMENT ON VIEW v_funding_book IS 'Joined funding lifecycle book view across credits/loans, interest allocations, capital events, and trades';

COMMIT;
