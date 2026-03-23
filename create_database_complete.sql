-- ============================================================
-- TRADING ENGINE DB - COMPLETE CREATE SCRIPT
-- ============================================================
-- Ovaj script kreira kompletnu bazu sa svim tabelama, indexima i view-ovima
-- Uključuje sve promene: exchange kolone, composite key za swing_positions, crypto tabele
-- ============================================================

BEGIN;

-- ============================================================
-- 1) Drop existing objects (if any)
-- ============================================================

DROP VIEW IF EXISTS v_funding_shadow_vs_actual CASCADE;
DROP VIEW IF EXISTS v_funding_book CASCADE;
DROP VIEW IF EXISTS v_signal_vs_fill CASCADE;

DROP TABLE IF EXISTS
    funding_reconciliation_log,
    funding_runtime_health,
    funding_shadow_plans,
    funding_capital_events,
    funding_interest_allocations,
    funding_interest_ledger,
    funding_trades,
    funding_loans,
    funding_credits,
    funding_offer_events,
    funding_offers,
    funding_offer_actions,
    funding_market_snapshots,
    funding_wallet_snapshots,
    signal_slayer_decisions,
    crypto_trades,
    crypto_snapshots,
    crypto_orderbooks,
    market_ticks,
    daily_pnl_crypto,
    daily_pnl,
    trade_signals,
    trade_journal,
    trade_fills,
    swing_positions,
    broker_orders,
    service_heartbeat
CASCADE;

-- ============================================================
-- 2) Core tables
-- ============================================================

-- 2.1 service_heartbeat
CREATE TABLE service_heartbeat (
    id           BIGSERIAL    PRIMARY KEY,
    service_name TEXT         NOT NULL,
    started_at   TIMESTAMPTZ  NOT NULL,
    host         TEXT,
    note         TEXT
);

-- 2.2 broker_orders
CREATE TABLE broker_orders (
    id              TEXT          PRIMARY KEY, -- corrId (sig-..., exit-...)
    broker_order_id TEXT,                      -- broker id (NULL dok ne postane "sent")
    symbol          TEXT          NOT NULL,
    exchange        TEXT,                      -- IBKR, Kraken, Bitfinex, Deribit
    side            TEXT          NOT NULL CHECK (side IN ('buy','sell')),
    qty             NUMERIC(18,8) NOT NULL CHECK (qty > 0),

    -- Order params
    order_type      TEXT          NOT NULL DEFAULT 'limit',
    limit_price     NUMERIC(18,8) CHECK (limit_price IS NULL OR limit_price > 0),
    stop_price      NUMERIC       NULL,

    status          TEXT          NOT NULL CHECK (
        status IN (
            'submitted',
            'sent',
            'partially_filled',
            'filled',
            'canceled',
            'expired',
            'rejected',
            'cancel-requested',
            'place-timeout',
            'place-error',
            'place-rolled-back'
        )
    ),

    created_utc     TIMESTAMPTZ   NOT NULL,
    sent_utc        TIMESTAMPTZ,
    filled_utc      TIMESTAMPTZ,
    canceled_utc    TIMESTAMPTZ,
    expired_utc     TIMESTAMPTZ,
    last_msg        TEXT,

    submit_bid      NUMERIC(18,8),
    submit_ask      NUMERIC(18,8),
    submit_spread   NUMERIC(18,8)
);

-- 2.3 swing_positions (sa composite key: symbol, exchange)
CREATE TABLE swing_positions (
    symbol               TEXT        NOT NULL,
    exchange             TEXT        NOT NULL,  -- IBKR, Kraken, Bitfinex, Deribit
    quantity             NUMERIC     NOT NULL,
    entry_price          NUMERIC     NOT NULL,
    opened_utc           TIMESTAMPTZ NOT NULL,
    strategy             TEXT        NOT NULL,
    correlation_id       TEXT        NOT NULL, -- entry corr (sig-...)
    planned_holding_days INTEGER     NOT NULL,
    exit_policy          TEXT        NOT NULL, -- None / PriceOnly / PriceOrTime / ManualOnly
    is_open              BOOLEAN     NOT NULL DEFAULT TRUE,
    closed_utc           TIMESTAMPTZ NULL,
    exit_reason          TEXT        NULL,
    auto_exit            BOOLEAN     NOT NULL DEFAULT FALSE,

    CONSTRAINT pk_swing_positions PRIMARY KEY (symbol, exchange)
);

-- 2.4 trade_fills
CREATE TABLE trade_fills (
    id                BIGSERIAL     PRIMARY KEY,
    utc               TIMESTAMPTZ   NOT NULL,
    symbol            TEXT          NOT NULL,
    exchange          TEXT,                      -- IBKR, Kraken, Bitfinex, Deribit
    side              TEXT          NOT NULL,          -- 'Buy' / 'Sell'
    quantity          NUMERIC(18,6) NOT NULL,
    price             NUMERIC(18,6) NOT NULL,
    notional          NUMERIC(18,6) NOT NULL,
    realized_pnl      NUMERIC(18,6) NOT NULL,
    is_paper          BOOLEAN       NOT NULL,
    is_exit           BOOLEAN       NOT NULL,
    strategy          TEXT,
    correlation_id    TEXT,
    broker_order_id   TEXT,
    estimated_fee_usd NUMERIC(18,6)
);

-- 2.5 trade_journal
CREATE TABLE trade_journal (
    id                 BIGSERIAL     PRIMARY KEY,
    utc                TIMESTAMPTZ   NOT NULL,
    symbol             TEXT          NOT NULL,
    exchange           TEXT,                      -- IBKR, Kraken, Bitfinex, Deribit
    side               TEXT          NOT NULL,
    quantity           NUMERIC(18,6) NOT NULL,
    price              NUMERIC(18,6) NOT NULL,
    notional           NUMERIC(18,6) NOT NULL,
    realized_pnl       NUMERIC(18,6) NOT NULL,
    is_paper           BOOLEAN       NOT NULL,
    is_exit            BOOLEAN       NOT NULL,
    strategy           TEXT,
    correlation_id     TEXT,
    broker_order_id    TEXT,
    estimated_fee_usd  NUMERIC(18,6),

    planned_price      NUMERIC(18,6),
    risk_fraction      NUMERIC(18,8),
    atr_used           NUMERIC(18,6),
    price_risk         NUMERIC(18,6)
);

-- 2.6 trade_signals
CREATE TABLE trade_signals (
    id               BIGSERIAL     PRIMARY KEY,
    utc              TIMESTAMPTZ   NOT NULL,
    symbol           TEXT          NOT NULL,
    exchange         TEXT,                      -- IBKR, Kraken, Bitfinex, Deribit
    side             TEXT          NOT NULL,
    suggested_price  NUMERIC(18,6),
    strategy         TEXT,
    reason           TEXT,
    accepted         BOOLEAN       NOT NULL,
    reject_reason    TEXT,
    planned_qty      NUMERIC(18,6),
    planned_notional NUMERIC(18,6),
    correlation_id   TEXT,

    run_env          TEXT,   -- Paper / Real
    rth_window       TEXT,   -- inside / outside / n/a
    trading_phase    TEXT    -- preRTH, open_1h, midday, power_hour, close, afterhours, off_hours
);

-- 2.7 daily_pnl (IBKR)
CREATE TABLE daily_pnl (
    trade_date   DATE           PRIMARY KEY,
    realized_pnl NUMERIC(18,8)  NOT NULL DEFAULT 0,
    total_fees   NUMERIC(18,8)  NOT NULL DEFAULT 0,
    trade_count  INT            NOT NULL DEFAULT 0,
    updated_utc  TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- 2.8 daily_pnl_crypto (Crypto)
CREATE TABLE daily_pnl_crypto (
    trade_date   DATE           PRIMARY KEY,
    realized_pnl NUMERIC(18,8)  NOT NULL DEFAULT 0,
    total_fees   NUMERIC(18,8)  NOT NULL DEFAULT 0,
    trade_count  INT            NOT NULL DEFAULT 0,
    updated_utc  TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- 2.9 market_ticks
CREATE TABLE market_ticks (
    id        BIGSERIAL    PRIMARY KEY,
    utc       TIMESTAMPTZ  NOT NULL,
    exchange  TEXT         NOT NULL,  -- IBKR, Kraken, Bitfinex, Deribit
    symbol    TEXT         NOT NULL,
    bid       NUMERIC(18,6),
    ask       NUMERIC(18,6),
    bid_size  NUMERIC(18,6),
    ask_size  NUMERIC(18,6)
);

-- 2.10 signal_slayer_decisions
CREATE TABLE signal_slayer_decisions (
    id               BIGSERIAL     PRIMARY KEY,
    utc              TIMESTAMPTZ   NOT NULL,
    symbol           TEXT          NOT NULL,
    exchange         TEXT,                      -- IBKR, Kraken, Bitfinex, Deribit
    strategy         TEXT          NOT NULL,
    accepted         BOOLEAN       NOT NULL,
    reason_code      TEXT          NOT NULL,

    price            NUMERIC(18,6),
    atr              NUMERIC(18,6),
    atr_fraction     NUMERIC(18,8),
    spread_bps       NUMERIC(18,4),
    activity_ticks   INT,
    regime           TEXT,

    slope5           NUMERIC(18,8),
    slope20          NUMERIC(18,8),

    run_env          TEXT,
    trading_phase    TEXT
);

-- 2.11 crypto_orderbooks
CREATE TABLE crypto_orderbooks (
    id           BIGSERIAL     PRIMARY KEY,
    utc          TIMESTAMPTZ   NOT NULL,
    exchange     TEXT          NOT NULL,
    symbol       TEXT          NOT NULL,
    bids         JSONB         NOT NULL,  -- [{price, size}, ...]
    asks         JSONB         NOT NULL,  -- [{price, size}, ...]
    spread       NUMERIC(18,8),
    mid_price    NUMERIC(18,8),
    bid_count    INT,
    ask_count    INT
);

-- 2.12 crypto_snapshots
CREATE TABLE crypto_snapshots (
    id           BIGSERIAL     PRIMARY KEY,
    utc          TIMESTAMPTZ   NOT NULL,
    exchange     TEXT          NOT NULL,
    symbol       TEXT          NOT NULL,
    snapshot_type TEXT         NOT NULL CHECK (snapshot_type ~ '^[a-z][a-z0-9_]*$'),
    data         JSONB         NOT NULL,  -- fleksibilni JSON sa snapshot podacima
    metadata     JSONB         -- dodatni metadata (npr. sequence number, checksum)
);

-- 2.13 crypto_trades
CREATE TABLE crypto_trades (
    id         BIGSERIAL     PRIMARY KEY,
    utc        TIMESTAMPTZ   NOT NULL,
    exchange   TEXT          NOT NULL,
    symbol     TEXT          NOT NULL,
    price      NUMERIC(18,8) NOT NULL,
    quantity   NUMERIC(18,8) NOT NULL,
    side       TEXT          NOT NULL CHECK (side IN ('buy', 'sell')),
    trade_id   BIGINT        NULL
);

-- 2.14 funding_wallet_snapshots
CREATE TABLE funding_wallet_snapshots (
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

-- 2.15 funding_market_snapshots
CREATE TABLE funding_market_snapshots (
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

-- 2.16 funding_offer_actions
CREATE TABLE funding_offer_actions (
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

-- 2.17 funding_offers
CREATE TABLE funding_offers (
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

-- 2.18 funding_offer_events
CREATE TABLE funding_offer_events (
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

-- 2.19 funding_credits
CREATE TABLE funding_credits (
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

-- 2.20 funding_loans
CREATE TABLE funding_loans (
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

-- 2.21 funding_trades
CREATE TABLE funding_trades (
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

-- 2.22 funding_interest_ledger
CREATE TABLE funding_interest_ledger (
    id                BIGSERIAL       PRIMARY KEY,
    utc               TIMESTAMPTZ     NOT NULL,
    exchange          TEXT            NOT NULL,
    ledger_id         BIGINT,
    currency          TEXT            NOT NULL,
    wallet_type       TEXT,
    symbol            TEXT,
    entry_type        TEXT            NOT NULL,
    credit_id         BIGINT,
    loan_id           BIGINT,
    funding_trade_id  BIGINT,
    raw_amount        NUMERIC(18,12),
    balance_after     NUMERIC(18,12),
    gross_interest    NUMERIC(18,12)  NOT NULL DEFAULT 0,
    fee_amount        NUMERIC(18,12)  NOT NULL DEFAULT 0,
    net_interest      NUMERIC(18,12)  NOT NULL DEFAULT 0,
    description       TEXT,
    metadata          JSONB,

    CONSTRAINT ux_funding_interest_ledger_exchange_ledger_id UNIQUE (exchange, ledger_id)
);

-- 2.23 funding_interest_allocations
CREATE TABLE funding_interest_allocations (
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

-- 2.24 funding_capital_events
CREATE TABLE funding_capital_events (
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

-- 2.25 funding_shadow_plans
CREATE TABLE funding_shadow_plans (
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

-- 2.26 funding_runtime_health
CREATE TABLE funding_runtime_health (
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

-- 2.27 funding_reconciliation_log
CREATE TABLE funding_reconciliation_log (
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

-- ============================================================
-- 3) Indexes
-- ============================================================

-- broker_orders
CREATE INDEX ix_broker_orders_status_created
    ON broker_orders (status, created_utc);

CREATE INDEX ix_broker_orders_symbol_created
    ON broker_orders (symbol, created_utc);

CREATE INDEX ix_broker_orders_broker_order_id
    ON broker_orders (broker_order_id);

CREATE INDEX idx_broker_orders_symbol_status_time
    ON broker_orders (symbol, status, created_utc DESC);

CREATE INDEX idx_broker_orders_status
    ON broker_orders (status);

CREATE INDEX ix_broker_orders_broker_order_id_notnull
    ON broker_orders (broker_order_id)
    WHERE broker_order_id IS NOT NULL;

CREATE INDEX ix_broker_orders_exchange_symbol
    ON broker_orders (exchange, symbol, created_utc DESC)
    WHERE exchange IS NOT NULL;

-- trade_fills
CREATE INDEX idx_trade_fills_symbol_utc
    ON trade_fills (symbol, utc DESC);

CREATE INDEX idx_trade_fills_corr
    ON trade_fills (correlation_id);

CREATE INDEX idx_trade_fills_broker_order_id
    ON trade_fills (broker_order_id);

CREATE INDEX ix_trade_fills_utc
    ON trade_fills (utc);

CREATE INDEX ix_trade_fills_exchange_symbol_utc
    ON trade_fills (exchange, symbol, utc DESC)
    WHERE exchange IS NOT NULL;

-- trade_journal
CREATE INDEX ix_trade_journal_utc
    ON trade_journal (utc DESC);

CREATE INDEX ix_trade_journal_symbol_utc
    ON trade_journal (symbol, utc DESC);

CREATE INDEX ix_trade_journal_corr
    ON trade_journal (correlation_id);

CREATE INDEX ix_trade_journal_broker_order_id
    ON trade_journal (broker_order_id);

CREATE INDEX ix_trade_journal_exchange_symbol_utc
    ON trade_journal (exchange, symbol, utc DESC)
    WHERE exchange IS NOT NULL;

-- trade_signals
CREATE INDEX ix_trade_signals_symbol_utc
    ON trade_signals (symbol, utc DESC);

CREATE INDEX ix_trade_signals_corr
    ON trade_signals (correlation_id);

CREATE INDEX ix_trade_signals_flags
    ON trade_signals (accepted, run_env);

CREATE INDEX ix_trade_signals_utc
    ON trade_signals (utc);

CREATE INDEX ix_trade_signals_exchange_symbol_utc
    ON trade_signals (exchange, symbol, utc DESC)
    WHERE exchange IS NOT NULL;

-- daily_pnl
CREATE INDEX ix_daily_pnl_updated
    ON daily_pnl (updated_utc);

-- daily_pnl_crypto
CREATE INDEX idx_daily_pnl_crypto_trade_date
    ON daily_pnl_crypto (trade_date);

-- service_heartbeat
CREATE INDEX ix_service_heartbeat_started
    ON service_heartbeat (started_at);

-- market_ticks
CREATE INDEX idx_market_ticks_exch_symbol_utc
    ON market_ticks (exchange, symbol, utc DESC);

-- signal_slayer_decisions
CREATE INDEX ix_signal_slayer_decisions_symbol_utc
    ON signal_slayer_decisions (symbol, utc DESC);

CREATE INDEX ix_signal_slayer_decisions_reason_code_utc
    ON signal_slayer_decisions (reason_code, utc DESC);

CREATE INDEX ix_signal_slayer_decisions_accepted_utc
    ON signal_slayer_decisions (accepted, utc DESC);

CREATE INDEX ix_signal_slayer_decisions_strategy_symbol
    ON signal_slayer_decisions (strategy, symbol, utc DESC);

CREATE INDEX ix_signal_slayer_decisions_exchange_symbol_utc
    ON signal_slayer_decisions (exchange, symbol, utc DESC)
    WHERE exchange IS NOT NULL;

-- swing_positions
CREATE INDEX ix_swing_positions_exchange
    ON swing_positions (exchange)
    WHERE exchange IS NOT NULL;

-- crypto_orderbooks
CREATE INDEX ix_crypto_orderbooks_exchange_symbol_utc
    ON crypto_orderbooks (exchange, symbol, utc DESC);

CREATE INDEX ix_crypto_orderbooks_utc
    ON crypto_orderbooks (utc DESC);

-- crypto_snapshots
CREATE INDEX ix_crypto_snapshots_exchange_symbol_type_utc
    ON crypto_snapshots (exchange, symbol, snapshot_type, utc DESC);

CREATE INDEX ix_crypto_snapshots_utc
    ON crypto_snapshots (utc DESC);

-- crypto_trades
CREATE INDEX ix_crypto_trades_exchange_symbol_utc
    ON crypto_trades (exchange, symbol, utc DESC);

CREATE INDEX ix_crypto_trades_utc
    ON crypto_trades (utc DESC);

CREATE INDEX ix_crypto_trades_trade_id
    ON crypto_trades (exchange, trade_id)
    WHERE trade_id IS NOT NULL;

CREATE UNIQUE INDEX ux_crypto_trades_exchange_symbol_trade_id_notnull
    ON crypto_trades (exchange, symbol, trade_id)
    WHERE trade_id IS NOT NULL;

-- funding_wallet_snapshots
CREATE INDEX ix_funding_wallet_snapshots_exchange_wallet_ccy_utc
    ON funding_wallet_snapshots (exchange, wallet_type, currency, utc DESC);

CREATE INDEX ix_funding_wallet_snapshots_utc
    ON funding_wallet_snapshots (utc DESC);

-- funding_market_snapshots
CREATE INDEX ix_funding_market_snapshots_exchange_symbol_utc
    ON funding_market_snapshots (exchange, symbol, utc DESC);

CREATE INDEX ix_funding_market_snapshots_utc
    ON funding_market_snapshots (utc DESC);

-- funding_offer_actions
CREATE INDEX ix_funding_offer_actions_exchange_symbol_utc
    ON funding_offer_actions (exchange, symbol, utc DESC);

CREATE INDEX ix_funding_offer_actions_action_utc
    ON funding_offer_actions (action, utc DESC);

-- funding_offers
CREATE INDEX ix_funding_offers_exchange_symbol_updated
    ON funding_offers (exchange, symbol, updated_utc DESC);

CREATE INDEX ix_funding_offers_status_updated
    ON funding_offers (status, updated_utc DESC);

-- funding_offer_events
CREATE INDEX ix_funding_offer_events_exchange_offer_utc
    ON funding_offer_events (exchange, offer_id, utc DESC);

CREATE INDEX ix_funding_offer_events_exchange_symbol_utc
    ON funding_offer_events (exchange, symbol, utc DESC);

-- funding_credits
CREATE INDEX ix_funding_credits_exchange_symbol_updated
    ON funding_credits (exchange, symbol, updated_utc DESC);

CREATE INDEX ix_funding_credits_status_updated
    ON funding_credits (status, updated_utc DESC);

-- funding_loans
CREATE INDEX ix_funding_loans_exchange_symbol_updated
    ON funding_loans (exchange, symbol, updated_utc DESC);

CREATE INDEX ix_funding_loans_status_updated
    ON funding_loans (status, updated_utc DESC);

-- funding_trades
CREATE INDEX ix_funding_trades_exchange_symbol_utc
    ON funding_trades (exchange, symbol, utc DESC);

CREATE INDEX ix_funding_trades_utc
    ON funding_trades (utc DESC);

-- funding_interest_ledger
CREATE INDEX ix_funding_interest_ledger_exchange_symbol_utc
    ON funding_interest_ledger (exchange, symbol, utc DESC);

CREATE INDEX ix_funding_interest_ledger_currency_utc
    ON funding_interest_ledger (currency, utc DESC);

-- funding_interest_allocations
CREATE INDEX ix_funding_interest_allocations_exchange_symbol_utc
    ON funding_interest_allocations (exchange, symbol, utc DESC);

CREATE INDEX ix_funding_interest_allocations_ledger_id
    ON funding_interest_allocations (ledger_id);

-- funding_capital_events
CREATE INDEX ix_funding_capital_events_exchange_symbol_utc
    ON funding_capital_events (exchange, symbol, utc DESC);

CREATE INDEX ix_funding_capital_events_type_utc
    ON funding_capital_events (event_type, utc DESC);

-- funding_shadow_plans
CREATE INDEX ix_funding_shadow_plans_exchange_symbol_utc
    ON funding_shadow_plans (exchange, symbol, utc DESC);

CREATE INDEX ix_funding_shadow_plans_bucket_utc
    ON funding_shadow_plans (bucket, utc DESC);

-- funding_runtime_health
CREATE INDEX ix_funding_runtime_health_exchange_utc
    ON funding_runtime_health (exchange, utc DESC);

-- funding_reconciliation_log
CREATE INDEX ix_funding_reconciliation_log_exchange_completed
    ON funding_reconciliation_log (exchange, completed_utc DESC);

-- ============================================================
-- 4) Views
-- ============================================================

CREATE VIEW v_signal_vs_fill AS
SELECT
    s.utc               AS signal_utc,
    s.symbol,
    s.exchange,
    s.side,
    s.strategy,
    s.accepted,
    s.reject_reason,
    s.correlation_id,
    j.utc               AS fill_utc,
    j.price             AS fill_price,
    j.quantity          AS fill_qty,
    j.notional          AS fill_notional,
    j.realized_pnl,
    j.is_paper,
    j.is_exit
FROM trade_signals s
LEFT JOIN trade_journal j
       ON j.correlation_id = s.correlation_id
ORDER BY s.utc DESC;

CREATE VIEW v_funding_book AS
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

CREATE VIEW v_funding_shadow_vs_actual AS
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

-- ============================================================
-- 5) Comments
-- ============================================================

COMMENT ON TABLE daily_pnl_crypto IS 'Daily PnL tracking for crypto trading (separate from IBKR daily_pnl)';
COMMENT ON TABLE swing_positions IS 'Swing positions with composite key (symbol, exchange) - allows same symbol on different exchanges';
COMMENT ON COLUMN swing_positions.exchange IS 'Exchange identifier: IBKR, Kraken, Bitfinex, Deribit';
COMMENT ON TABLE funding_wallet_snapshots IS 'Funding wallet balances kept separate from spot trading state';
COMMENT ON TABLE funding_offers IS 'Latest-state funding offers table, separate from broker_orders';
COMMENT ON TABLE funding_offer_events IS 'Funding offer lifecycle events from WS/REST';
COMMENT ON TABLE funding_interest_ledger IS 'Funding income ledger for interest/payout tracking';
COMMENT ON TABLE funding_interest_allocations IS 'Normalized allocation of raw funding payments across credit/loan lifecycles';
COMMENT ON TABLE funding_capital_events IS 'Business-level funding book events: principal deployed, returned, and interest paid';
COMMENT ON TABLE funding_shadow_plans IS 'Shadow-only funding engine plans used to compare Motor/Opportunistic ideas against actual funding outcomes';
COMMENT ON VIEW v_funding_book IS 'Joined funding lifecycle book view across credits/loans, interest allocations, capital events, and trades';
COMMENT ON VIEW v_funding_shadow_vs_actual IS 'Latest shadow funding plan per symbol compared with realized funding-book outcomes';

COMMIT;

-- ============================================================
-- 6) TimescaleDB hypertables + kompresija (OPCIONO)
--    Ako koristiš Timescale, otkomentariši sledeće:
-- ============================================================
-- CREATE EXTENSION IF NOT EXISTS timescaledb;
-- SELECT create_hypertable('trade_fills',   by_range('utc', INTERVAL '1 day'), if_not_exists => TRUE);
-- SELECT create_hypertable('trade_signals', by_range('utc', INTERVAL '1 day'), if_not_exists => TRUE);
-- SELECT create_hypertable('trade_journal', by_range('utc', INTERVAL '1 day'), if_not_exists => TRUE);
-- SELECT create_hypertable('market_ticks',  by_range('utc', INTERVAL '1 day'), if_not_exists => TRUE);
-- ALTER TABLE trade_fills   SET (timescaledb.compress, timescaledb.compress_segmentby = 'symbol');
-- ALTER TABLE trade_signals SET (timescaledb.compress, timescaledb.compress_segmentby = 'symbol');
-- ALTER TABLE trade_journal SET (timescaledb.compress, timescaledb.compress_segmentby = 'symbol');
-- ALTER TABLE market_ticks  SET (timescaledb.compress, timescaledb.compress_segmentby = 'exchange, symbol');
-- SELECT add_compression_policy('trade_fills',   INTERVAL '7 days', if_not_exists => TRUE);
-- SELECT add_compression_policy('trade_signals', INTERVAL '7 days', if_not_exists => TRUE);
-- SELECT add_compression_policy('trade_journal', INTERVAL '7 days', if_not_exists => TRUE);
-- SELECT add_compression_policy('market_ticks',  INTERVAL '7 days', if_not_exists => TRUE);

