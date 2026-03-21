BEGIN;

CREATE TABLE IF NOT EXISTS funding_wallet_snapshots (
    id           BIGSERIAL      PRIMARY KEY,
    utc          TIMESTAMPTZ    NOT NULL,
    exchange     TEXT           NOT NULL,
    wallet_type  TEXT           NOT NULL,
    currency     TEXT           NOT NULL,
    total        NUMERIC(18,8)  NOT NULL,
    available    NUMERIC(18,8)  NOT NULL,
    reserved     NUMERIC(18,8)  NOT NULL,
    source       TEXT           NOT NULL DEFAULT 'runtime',
    metadata     JSONB
);

CREATE INDEX IF NOT EXISTS ix_funding_wallet_snapshots_exchange_wallet_ccy_utc
    ON funding_wallet_snapshots (exchange, wallet_type, currency, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_wallet_snapshots_utc
    ON funding_wallet_snapshots (utc DESC);

CREATE TABLE IF NOT EXISTS funding_market_snapshots (
    id               BIGSERIAL       PRIMARY KEY,
    utc              TIMESTAMPTZ     NOT NULL,
    exchange         TEXT            NOT NULL,
    symbol           TEXT            NOT NULL,
    frr              NUMERIC(18,12),
    bid_rate         NUMERIC(18,12)  NOT NULL,
    bid_period_days  INT             NOT NULL,
    bid_size         NUMERIC(18,8)   NOT NULL,
    ask_rate         NUMERIC(18,12)  NOT NULL,
    ask_period_days  INT             NOT NULL,
    ask_size         NUMERIC(18,8)   NOT NULL,
    metadata         JSONB
);

CREATE INDEX IF NOT EXISTS ix_funding_market_snapshots_exchange_symbol_utc
    ON funding_market_snapshots (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_market_snapshots_utc
    ON funding_market_snapshots (utc DESC);

CREATE TABLE IF NOT EXISTS funding_offer_actions (
    id                 BIGSERIAL       PRIMARY KEY,
    utc                TIMESTAMPTZ     NOT NULL,
    exchange           TEXT            NOT NULL,
    symbol             TEXT            NOT NULL,
    action             TEXT            NOT NULL,
    dry_run            BOOLEAN         NOT NULL,
    is_actionable      BOOLEAN         NOT NULL DEFAULT FALSE,
    currency           TEXT,
    wallet_type        TEXT,
    available_balance  NUMERIC(18,8),
    lendable_balance   NUMERIC(18,8),
    amount             NUMERIC(18,8),
    rate               NUMERIC(18,12),
    period_days        INT,
    reason             TEXT,
    offer_id           BIGINT,
    correlation_id     TEXT,
    metadata           JSONB
);

CREATE INDEX IF NOT EXISTS ix_funding_offer_actions_exchange_symbol_utc
    ON funding_offer_actions (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_offer_actions_action_utc
    ON funding_offer_actions (action, utc DESC);

CREATE TABLE IF NOT EXISTS funding_offers (
    id               BIGSERIAL       PRIMARY KEY,
    exchange         TEXT            NOT NULL,
    offer_id         BIGINT          NOT NULL,
    symbol           TEXT            NOT NULL,
    currency         TEXT,
    wallet_type      TEXT,
    offer_type       TEXT,
    status           TEXT            NOT NULL,
    amount           NUMERIC(18,8)   NOT NULL,
    original_amount  NUMERIC(18,8),
    rate             NUMERIC(18,12)  NOT NULL,
    rate_real        NUMERIC(18,12),
    period_days      INT             NOT NULL,
    flags            INT             NOT NULL DEFAULT 0,
    notify           BOOLEAN         NOT NULL DEFAULT FALSE,
    hidden           BOOLEAN         NOT NULL DEFAULT FALSE,
    renew            BOOLEAN         NOT NULL DEFAULT FALSE,
    is_active        BOOLEAN         NOT NULL DEFAULT FALSE,
    created_utc      TIMESTAMPTZ,
    updated_utc      TIMESTAMPTZ,
    closed_utc       TIMESTAMPTZ,
    metadata         JSONB,

    CONSTRAINT ux_funding_offers_exchange_offer_id UNIQUE (exchange, offer_id)
);

CREATE INDEX IF NOT EXISTS ix_funding_offers_exchange_symbol_updated
    ON funding_offers (exchange, symbol, updated_utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_offers_status_updated
    ON funding_offers (status, updated_utc DESC);

CREATE TABLE IF NOT EXISTS funding_offer_events (
    id               BIGSERIAL       PRIMARY KEY,
    utc              TIMESTAMPTZ     NOT NULL,
    exchange         TEXT            NOT NULL,
    offer_id         BIGINT          NOT NULL,
    symbol           TEXT            NOT NULL,
    event_type       TEXT            NOT NULL,
    status           TEXT,
    amount           NUMERIC(18,8),
    original_amount  NUMERIC(18,8),
    rate             NUMERIC(18,12),
    rate_real        NUMERIC(18,12),
    period_days      INT,
    message          TEXT,
    metadata         JSONB
);

CREATE INDEX IF NOT EXISTS ix_funding_offer_events_exchange_offer_utc
    ON funding_offer_events (exchange, offer_id, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_offer_events_exchange_symbol_utc
    ON funding_offer_events (exchange, symbol, utc DESC);

CREATE TABLE IF NOT EXISTS funding_credits (
    id               BIGSERIAL       PRIMARY KEY,
    exchange         TEXT            NOT NULL,
    credit_id        BIGINT          NOT NULL,
    symbol           TEXT            NOT NULL,
    side             TEXT,
    status           TEXT            NOT NULL,
    amount           NUMERIC(18,8)   NOT NULL,
    original_amount  NUMERIC(18,8),
    rate             NUMERIC(18,12),
    period_days      INT,
    created_utc      TIMESTAMPTZ,
    updated_utc      TIMESTAMPTZ,
    opened_utc       TIMESTAMPTZ,
    closed_utc       TIMESTAMPTZ,
    metadata         JSONB,

    CONSTRAINT ux_funding_credits_exchange_credit_id UNIQUE (exchange, credit_id)
);

CREATE INDEX IF NOT EXISTS ix_funding_credits_exchange_symbol_updated
    ON funding_credits (exchange, symbol, updated_utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_credits_status_updated
    ON funding_credits (status, updated_utc DESC);

CREATE TABLE IF NOT EXISTS funding_loans (
    id               BIGSERIAL       PRIMARY KEY,
    exchange         TEXT            NOT NULL,
    loan_id          BIGINT          NOT NULL,
    symbol           TEXT            NOT NULL,
    side             TEXT,
    status           TEXT            NOT NULL,
    amount           NUMERIC(18,8)   NOT NULL,
    original_amount  NUMERIC(18,8),
    rate             NUMERIC(18,12),
    period_days      INT,
    created_utc      TIMESTAMPTZ,
    updated_utc      TIMESTAMPTZ,
    opened_utc       TIMESTAMPTZ,
    closed_utc       TIMESTAMPTZ,
    metadata         JSONB,

    CONSTRAINT ux_funding_loans_exchange_loan_id UNIQUE (exchange, loan_id)
);

CREATE INDEX IF NOT EXISTS ix_funding_loans_exchange_symbol_updated
    ON funding_loans (exchange, symbol, updated_utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_loans_status_updated
    ON funding_loans (status, updated_utc DESC);

CREATE TABLE IF NOT EXISTS funding_trades (
    id                BIGSERIAL       PRIMARY KEY,
    utc               TIMESTAMPTZ     NOT NULL,
    exchange          TEXT            NOT NULL,
    funding_trade_id  BIGINT          NOT NULL,
    symbol            TEXT            NOT NULL,
    offer_id          BIGINT,
    credit_id         BIGINT,
    loan_id           BIGINT,
    amount            NUMERIC(18,8)   NOT NULL,
    rate              NUMERIC(18,12),
    period_days       INT,
    maker             BOOLEAN,
    metadata          JSONB,

    CONSTRAINT ux_funding_trades_exchange_trade_id UNIQUE (exchange, funding_trade_id)
);

CREATE INDEX IF NOT EXISTS ix_funding_trades_exchange_symbol_utc
    ON funding_trades (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_trades_utc
    ON funding_trades (utc DESC);

CREATE TABLE IF NOT EXISTS funding_interest_ledger (
    id                BIGSERIAL       PRIMARY KEY,
    utc               TIMESTAMPTZ     NOT NULL,
    exchange          TEXT            NOT NULL,
    currency          TEXT            NOT NULL,
    symbol            TEXT,
    entry_type        TEXT            NOT NULL,
    credit_id         BIGINT,
    loan_id           BIGINT,
    funding_trade_id  BIGINT,
    gross_interest    NUMERIC(18,12)  NOT NULL DEFAULT 0,
    fee_amount        NUMERIC(18,12)  NOT NULL DEFAULT 0,
    net_interest      NUMERIC(18,12)  NOT NULL DEFAULT 0,
    metadata          JSONB
);

CREATE INDEX IF NOT EXISTS ix_funding_interest_ledger_exchange_symbol_utc
    ON funding_interest_ledger (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_interest_ledger_currency_utc
    ON funding_interest_ledger (currency, utc DESC);

CREATE TABLE IF NOT EXISTS funding_runtime_health (
    id                   BIGSERIAL    PRIMARY KEY,
    utc                  TIMESTAMPTZ  NOT NULL,
    exchange             TEXT         NOT NULL,
    ws_connected         BOOLEAN      NOT NULL,
    ws_last_message_utc  TIMESTAMPTZ,
    rest_last_sync_utc   TIMESTAMPTZ,
    error_count          INT          NOT NULL DEFAULT 0,
    degraded_mode        BOOLEAN      NOT NULL DEFAULT FALSE,
    self_disabled        BOOLEAN      NOT NULL DEFAULT FALSE,
    metadata             JSONB
);

CREATE INDEX IF NOT EXISTS ix_funding_runtime_health_exchange_utc
    ON funding_runtime_health (exchange, utc DESC);

CREATE TABLE IF NOT EXISTS funding_reconciliation_log (
    id               BIGSERIAL    PRIMARY KEY,
    started_utc      TIMESTAMPTZ  NOT NULL,
    completed_utc    TIMESTAMPTZ,
    exchange         TEXT         NOT NULL,
    symbol           TEXT,
    mismatch_count   INT          NOT NULL DEFAULT 0,
    corrected_count  INT          NOT NULL DEFAULT 0,
    severity         TEXT,
    summary          TEXT,
    metadata         JSONB
);

CREATE INDEX IF NOT EXISTS ix_funding_reconciliation_log_exchange_completed
    ON funding_reconciliation_log (exchange, completed_utc DESC);

COMMIT;
