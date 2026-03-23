-- ============================================================
-- TRADING ENGINE DB - RECREATE (CLEAN RESET)
-- WARNING: BRIŠE SVE TABELE + VIEW u ovoj šemi.
-- ============================================================

BEGIN;

-- 1) Drop views first
DROP VIEW IF EXISTS v_funding_shadow_session_vs_actual;
DROP VIEW IF EXISTS v_funding_shadow_action_vs_actual;
DROP VIEW IF EXISTS v_funding_shadow_vs_actual;
DROP VIEW IF EXISTS v_funding_book;
DROP VIEW IF EXISTS v_signal_vs_fill;

-- 2) Drop hypertables separately.
-- Timescale ne dozvoljava DROP hypertable zajedno sa drugim objektima u istoj naredbi.
DROP TABLE IF EXISTS crypto_orderbooks CASCADE;

-- 3) Drop remaining tables (CASCADE zbog potencijalnih zavisnosti)
DROP TABLE IF EXISTS
    funding_reconciliation_log,
    funding_runtime_health,
    funding_shadow_action_sessions,
    funding_shadow_actions,
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
-- 1) Core tables (fresh create)
-- ============================================================

-- 1.1 service_heartbeat
CREATE TABLE service_heartbeat (
    id           BIGSERIAL    PRIMARY KEY,
    service_name TEXT         NOT NULL,
    started_at   TIMESTAMPTZ  NOT NULL,
    host         TEXT,
    note         TEXT
);

-- 1.2 broker_orders
CREATE TABLE broker_orders (
    id              TEXT          PRIMARY KEY, -- corrId (sig-..., exit-...)
    broker_order_id TEXT,                      -- broker id (NULL dok ne postane "sent")
    symbol          TEXT          NOT NULL,
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

-- 1.3 swing_positions
CREATE TABLE swing_positions (
    symbol               TEXT        NOT NULL,
    exchange             TEXT        NOT NULL,
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

-- 1.4 trade_fills
CREATE TABLE trade_fills (
    id                BIGSERIAL     PRIMARY KEY,
    utc               TIMESTAMPTZ   NOT NULL,
    symbol            TEXT          NOT NULL,
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

-- 1.5 trade_journal
CREATE TABLE trade_journal (
    id                 BIGSERIAL     PRIMARY KEY,
    utc                TIMESTAMPTZ   NOT NULL,
    symbol             TEXT          NOT NULL,
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

-- 1.6 trade_signals
CREATE TABLE trade_signals (
    id               BIGSERIAL     PRIMARY KEY,
    utc              TIMESTAMPTZ   NOT NULL,
    symbol           TEXT          NOT NULL,
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

-- 1.7 daily_pnl
CREATE TABLE daily_pnl (
    trade_date   DATE           PRIMARY KEY,
    realized_pnl NUMERIC(18,8)  NOT NULL DEFAULT 0,
    total_fees   NUMERIC(18,8)  NOT NULL DEFAULT 0,
    trade_count  INT            NOT NULL DEFAULT 0,
    updated_utc  TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- Migration: Add daily_pnl_crypto table for crypto trading PnL
-- Date: 2026-01-04
-- Description: Separate table for crypto daily PnL to avoid mixing with IBKR PnL

CREATE TABLE IF NOT EXISTS daily_pnl_crypto (
    trade_date   DATE           NOT NULL,
    exchange     TEXT           NOT NULL,
    realized_pnl NUMERIC(18,8)  NOT NULL DEFAULT 0,
    total_fees   NUMERIC(18,8)  NOT NULL DEFAULT 0,
    trade_count  INT            NOT NULL DEFAULT 0,
    updated_utc  TIMESTAMPTZ    NOT NULL DEFAULT now(),
    CONSTRAINT pk_daily_pnl_crypto PRIMARY KEY (trade_date, exchange)
);

-- Index za brže upite po datumu i exchange-u
CREATE INDEX IF NOT EXISTS idx_daily_pnl_crypto_trade_date ON daily_pnl_crypto(trade_date);
CREATE INDEX IF NOT EXISTS idx_daily_pnl_crypto_exchange ON daily_pnl_crypto(exchange);

COMMENT ON TABLE daily_pnl_crypto IS 'Daily PnL tracking for crypto trading (separate from IBKR daily_pnl)';



-- 1.8 market_ticks
CREATE TABLE market_ticks (
    id        BIGSERIAL    PRIMARY KEY,
    utc       TIMESTAMPTZ  NOT NULL,
    exchange  TEXT         NOT NULL,
    symbol    TEXT         NOT NULL,
    bid       NUMERIC(18,6),
    ask       NUMERIC(18,6),
    bid_size  NUMERIC(18,6),
    ask_size  NUMERIC(18,6)
);

-- 1.9 signal_slayer_decisions
CREATE TABLE signal_slayer_decisions (
    id               BIGSERIAL     PRIMARY KEY,
    utc              TIMESTAMPTZ   NOT NULL,
    symbol           TEXT          NOT NULL,
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

-- ============================================================
-- 2) Indexes
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

-- trade_fills
CREATE INDEX idx_trade_fills_symbol_utc
    ON trade_fills (symbol, utc DESC);

CREATE INDEX idx_trade_fills_corr
    ON trade_fills (correlation_id);

CREATE INDEX idx_trade_fills_broker_order_id
    ON trade_fills (broker_order_id);

CREATE INDEX ix_trade_fills_utc
    ON trade_fills (utc);

-- trade_journal
CREATE INDEX ix_trade_journal_utc
    ON trade_journal (utc DESC);

CREATE INDEX ix_trade_journal_symbol_utc
    ON trade_journal (symbol, utc DESC);

CREATE INDEX ix_trade_journal_corr
    ON trade_journal (correlation_id);

CREATE INDEX ix_trade_journal_broker_order_id
    ON trade_journal (broker_order_id);

-- trade_signals
CREATE INDEX ix_trade_signals_symbol_utc
    ON trade_signals (symbol, utc DESC);

CREATE INDEX ix_trade_signals_corr
    ON trade_signals (correlation_id);

CREATE INDEX ix_trade_signals_flags
    ON trade_signals (accepted, run_env);

CREATE INDEX ix_trade_signals_utc
    ON trade_signals (utc);

-- daily_pnl
CREATE INDEX ix_daily_pnl_updated
    ON daily_pnl (updated_utc);

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

-- ============================================================
-- 3) View
-- ============================================================

CREATE VIEW v_signal_vs_fill AS
SELECT
    s.utc               AS signal_utc,
    s.symbol,
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

COMMIT;

-- ============================================================
-- 4) TimescaleDB hypertables + kompresija (OPCIONO)
--    Ako koristiš Timescale, otkomentariši sledeće:
-- ============================================================
-- CREATE EXTENSION IF NOT EXISTS timescaledb;
-- SELECT create_hypertable('trade_fills',   by_range('utc', INTERVAL '1 day'), if_not_exists => TRUE);
-- SELECT create_hypertable('trade_signals', by_range('utc', INTERVAL '1 day'), if_not_exists => TRUE);
-- SELECT create_hypertable('trade_journal', by_range('utc', INTERVAL '1 day'), if_not_exists => TRUE);
-- ALTER TABLE trade_fills   SET (timescaledb.compress, timescaledb.compress_segmentby = 'symbol');
-- ALTER TABLE trade_signals SET (timescaledb.compress, timescaledb.compress_segmentby = 'symbol');
-- ALTER TABLE trade_journal SET (timescaledb.compress, timescaledb.compress_segmentby = 'symbol');
-- SELECT add_compression_policy('trade_fills',   INTERVAL '7 days', if_not_exists => TRUE);
-- SELECT add_compression_policy('trade_signals', INTERVAL '7 days', if_not_exists => TRUE);
-- SELECT add_compression_policy('trade_journal', INTERVAL '7 days', if_not_exists => TRUE);

-- ============================================================
-- 5) ALTER statements - Crypto support (migration)
--    Dodaj ove ALTER-e na postojeću bazu
-- ============================================================

-- 5.1 Dodaj exchange kolonu u postojeće tabele (ako već nije dodato)
ALTER TABLE broker_orders ADD COLUMN IF NOT EXISTS exchange TEXT;
ALTER TABLE trade_fills ADD COLUMN IF NOT EXISTS exchange TEXT;
ALTER TABLE trade_journal ADD COLUMN IF NOT EXISTS exchange TEXT;
ALTER TABLE trade_signals ADD COLUMN IF NOT EXISTS exchange TEXT;
ALTER TABLE swing_positions ADD COLUMN IF NOT EXISTS exchange TEXT;
-- Ako exchange kolona već postoji ali nije NOT NULL, postavi je na NOT NULL
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'swing_positions' 
        AND column_name = 'exchange' 
        AND is_nullable = 'YES'
    ) THEN
        -- Prvo popuni NULL vrednosti za postojeće redove
        UPDATE swing_positions SET exchange = 'SMART' WHERE exchange IS NULL;
        -- Zatim postavi NOT NULL
        ALTER TABLE swing_positions ALTER COLUMN exchange SET NOT NULL;
    END IF;
END $$;
ALTER TABLE signal_slayer_decisions ADD COLUMN IF NOT EXISTS exchange TEXT;

-- Migration: Add exchange column to daily_pnl_crypto (2026-01-12)
-- Description: Separate PnL tracking per exchange (Kraken, Bitfinex, Deribit, Bybit)
DO $$
BEGIN
    -- Ako tabela postoji ali nema exchange kolonu, dodaj je
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'daily_pnl_crypto')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'daily_pnl_crypto' AND column_name = 'exchange')
    THEN
        -- Prvo dodaj exchange kolonu sa default vrednošću
        ALTER TABLE daily_pnl_crypto ADD COLUMN exchange TEXT NOT NULL DEFAULT 'Crypto';
        
        -- Obriši staru PRIMARY KEY constraint
        ALTER TABLE daily_pnl_crypto DROP CONSTRAINT IF EXISTS daily_pnl_crypto_pkey;
        
        -- Dodaj novu PRIMARY KEY sa (trade_date, exchange)
        ALTER TABLE daily_pnl_crypto ADD CONSTRAINT pk_daily_pnl_crypto PRIMARY KEY (trade_date, exchange);
        
        -- Dodaj index za exchange
        CREATE INDEX IF NOT EXISTS idx_daily_pnl_crypto_exchange ON daily_pnl_crypto(exchange);
        
        -- Ukloni default nakon što je kolona popunjena
        ALTER TABLE daily_pnl_crypto ALTER COLUMN exchange DROP DEFAULT;
    END IF;
END $$;

-- 5.2 Indexi za exchange kolone
CREATE INDEX IF NOT EXISTS ix_broker_orders_exchange_symbol
    ON broker_orders (exchange, symbol, created_utc DESC)
    WHERE exchange IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_trade_fills_exchange_symbol_utc
    ON trade_fills (exchange, symbol, utc DESC)
    WHERE exchange IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_trade_journal_exchange_symbol_utc
    ON trade_journal (exchange, symbol, utc DESC)
    WHERE exchange IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_trade_signals_exchange_symbol_utc
    ON trade_signals (exchange, symbol, utc DESC)
    WHERE exchange IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_swing_positions_exchange
    ON swing_positions (exchange)
    WHERE exchange IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_signal_slayer_decisions_exchange_symbol_utc
    ON signal_slayer_decisions (exchange, symbol, utc DESC)
    WHERE exchange IS NOT NULL;

-- 5.3 Crypto orderbooks tabela
CREATE TABLE IF NOT EXISTS crypto_orderbooks (
    id           BIGSERIAL     NOT NULL,
    utc          TIMESTAMPTZ   NOT NULL,
    exchange     TEXT          NOT NULL,
    symbol       TEXT          NOT NULL,
    bids         JSONB         NOT NULL,  -- [{price, size}, ...]
    asks         JSONB         NOT NULL,  -- [{price, size}, ...]
    spread       NUMERIC(18,8),
    mid_price    NUMERIC(18,8),
    bid_count    INT,
    ask_count    INT,

    CONSTRAINT pk_crypto_orderbooks PRIMARY KEY (utc, id)
);

CREATE INDEX IF NOT EXISTS ix_crypto_orderbooks_exchange_symbol_utc
    ON crypto_orderbooks (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_crypto_orderbooks_utc
    ON crypto_orderbooks (utc DESC);

-- 5.4 Crypto snapshots tabela (za orderbook/ticker snapshot-e)
CREATE TABLE IF NOT EXISTS crypto_snapshots (
    id           BIGSERIAL     PRIMARY KEY,
    utc          TIMESTAMPTZ   NOT NULL,
    exchange     TEXT          NOT NULL,
    symbol       TEXT          NOT NULL,
    snapshot_type TEXT         NOT NULL CHECK (snapshot_type ~ '^[a-z][a-z0-9_]*$'),
    data         JSONB         NOT NULL,  -- fleksibilni JSON sa snapshot podacima
    metadata     JSONB         -- dodatni metadata (npr. sequence number, checksum)
);

CREATE INDEX IF NOT EXISTS ix_crypto_snapshots_exchange_symbol_type_utc
    ON crypto_snapshots (exchange, symbol, snapshot_type, utc DESC);

CREATE INDEX IF NOT EXISTS ix_crypto_snapshots_utc
    ON crypto_snapshots (utc DESC);

-- 5.5 Crypto trades (raw trade ticks za pullback / analizu; batch insert)
CREATE TABLE IF NOT EXISTS crypto_trades (
    id         BIGSERIAL     PRIMARY KEY,
    utc        TIMESTAMPTZ   NOT NULL,
    exchange   TEXT          NOT NULL,
    symbol     TEXT          NOT NULL,
    price      NUMERIC(18,8) NOT NULL,
    quantity   NUMERIC(18,8) NOT NULL,
    side       TEXT          NOT NULL CHECK (side IN ('buy', 'sell')),
    trade_id   BIGINT        NULL  -- exchange trade id (Bitfinex itd.) za dedupe i analizu
);

CREATE INDEX IF NOT EXISTS ix_crypto_trades_exchange_symbol_utc
    ON crypto_trades (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_crypto_trades_utc
    ON crypto_trades (utc DESC);

CREATE INDEX IF NOT EXISTS ix_crypto_trades_trade_id
    ON crypto_trades (exchange, trade_id)
    WHERE trade_id IS NOT NULL;

-- Fresh DBs: enforce uniqueness for exchange-provided trade ids (e.g. Bitfinex trade_id)
-- so te/tu and snapshot/live overlaps cannot duplicate rows in crypto_trades.
CREATE UNIQUE INDEX IF NOT EXISTS ux_crypto_trades_exchange_symbol_trade_id_notnull
    ON crypto_trades (exchange, symbol, trade_id)
    WHERE trade_id IS NOT NULL;

-- 5.6 Funding wallet snapshots
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

-- 5.7 Funding market snapshots
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

-- 5.8 Funding offer actions / engine intent
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

-- 5.9 Funding offers latest-state table
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

-- 5.10 Funding offer events
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

-- 5.11 Funding credits
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

-- 5.12 Funding loans
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

-- 5.13 Funding trades
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

-- 5.14 Funding interest / payout ledger
CREATE TABLE IF NOT EXISTS funding_interest_ledger (
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

CREATE INDEX IF NOT EXISTS ix_funding_interest_ledger_exchange_symbol_utc
    ON funding_interest_ledger (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_interest_ledger_currency_utc
    ON funding_interest_ledger (currency, utc DESC);

-- 5.15 Funding interest allocations
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

-- 5.16 Funding capital events
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

-- 5.17 Funding shadow plans
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

-- 5.18 Funding shadow actions
CREATE TABLE IF NOT EXISTS funding_shadow_actions (
    id                   BIGSERIAL       PRIMARY KEY,
    utc                  TIMESTAMPTZ     NOT NULL,
    exchange             TEXT            NOT NULL,
    action_key           TEXT            NOT NULL,
    plan_key             TEXT            NOT NULL,
    symbol               TEXT            NOT NULL,
    currency             TEXT            NOT NULL,
    regime               TEXT            NOT NULL,
    bucket               TEXT            NOT NULL,
    action               TEXT            NOT NULL,
    is_actionable        BOOLEAN         NOT NULL,
    available_balance    NUMERIC(18,8)   NOT NULL,
    lendable_balance     NUMERIC(18,8)   NOT NULL,
    allocation_amount    NUMERIC(18,8)   NOT NULL DEFAULT 0,
    allocation_fraction  NUMERIC(18,12)  NOT NULL DEFAULT 0,
    target_rate          NUMERIC(18,12),
    fallback_rate        NUMERIC(18,12),
    target_period_days   INT,
    max_wait_minutes     INT,
    decision_deadline_utc TIMESTAMPTZ,
    role                 TEXT,
    fallback_bucket      TEXT,
    active_offer_count   INT             NOT NULL DEFAULT 0,
    active_offer_id      BIGINT,
    active_offer_rate    NUMERIC(18,12),
    active_offer_amount  NUMERIC(18,8),
    active_offer_status  TEXT,
    reason               TEXT            NOT NULL,
    summary              TEXT,
    metadata             JSONB,

    CONSTRAINT ux_funding_shadow_actions_exchange_key UNIQUE (exchange, action_key)
);

CREATE INDEX IF NOT EXISTS ix_funding_shadow_actions_exchange_symbol_utc
    ON funding_shadow_actions (exchange, symbol, utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_shadow_actions_action_utc
    ON funding_shadow_actions (action, utc DESC);

-- 5.19 Funding shadow action sessions
CREATE TABLE IF NOT EXISTS funding_shadow_action_sessions (
    id                   BIGSERIAL       PRIMARY KEY,
    exchange             TEXT            NOT NULL,
    session_key          TEXT            NOT NULL,
    symbol               TEXT            NOT NULL,
    currency             TEXT            NOT NULL,
    bucket               TEXT            NOT NULL,
    first_regime         TEXT            NOT NULL,
    current_regime       TEXT            NOT NULL,
    first_action         TEXT            NOT NULL,
    current_action       TEXT            NOT NULL,
    status               TEXT            NOT NULL,
    is_actionable        BOOLEAN         NOT NULL,
    available_balance    NUMERIC(18,8)   NOT NULL,
    lendable_balance     NUMERIC(18,8)   NOT NULL,
    allocation_amount    NUMERIC(18,8)   NOT NULL DEFAULT 0,
    allocation_fraction  NUMERIC(18,12)  NOT NULL DEFAULT 0,
    target_rate_initial  NUMERIC(18,12),
    target_rate_current  NUMERIC(18,12),
    fallback_rate        NUMERIC(18,12),
    target_period_days   INT,
    max_wait_minutes     INT,
    opened_utc           TIMESTAMPTZ     NOT NULL,
    last_updated_utc     TIMESTAMPTZ     NOT NULL,
    decision_deadline_utc TIMESTAMPTZ,
    closed_utc           TIMESTAMPTZ,
    active_offer_id      BIGINT,
    active_offer_rate    NUMERIC(18,12),
    active_offer_amount  NUMERIC(18,8),
    active_offer_status  TEXT,
    resolution           TEXT,
    update_count         INT             NOT NULL DEFAULT 1,
    summary              TEXT            NOT NULL,
    metadata             JSONB,

    CONSTRAINT ux_funding_shadow_action_sessions_exchange_key UNIQUE (exchange, session_key)
);

CREATE INDEX IF NOT EXISTS ix_funding_shadow_action_sessions_exchange_symbol_opened
    ON funding_shadow_action_sessions (exchange, symbol, opened_utc DESC);

CREATE INDEX IF NOT EXISTS ix_funding_shadow_action_sessions_status_opened
    ON funding_shadow_action_sessions (status, opened_utc DESC);

-- 5.20 Funding runtime health
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

-- 5.21 Funding reconciliation log
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

CREATE OR REPLACE VIEW v_funding_shadow_action_vs_actual AS
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

CREATE OR REPLACE VIEW v_funding_shadow_session_vs_actual AS
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

COMMENT ON TABLE funding_shadow_plans IS 'Shadow-only funding engine plans used to compare Motor/Opportunistic ideas against actual funding outcomes';
COMMENT ON VIEW v_funding_shadow_vs_actual IS 'Latest shadow funding plan per symbol compared with realized funding-book outcomes';
COMMENT ON TABLE funding_shadow_actions IS 'Shadow-only next-step action policy for each funding bucket, kept separate from live offer execution';
COMMENT ON VIEW v_funding_shadow_action_vs_actual IS 'Latest shadow next-step action policy per symbol compared with latest live offer behavior and funding-book outcomes';
COMMENT ON TABLE funding_shadow_action_sessions IS 'Stateful shadow-action lifecycles that track wait/place/fallback sessions across consecutive funding cycles';
COMMENT ON VIEW v_funding_shadow_session_vs_actual IS 'Latest shadow action session per symbol compared with live offer behavior and realized funding-book outcomes';

-- NOTE (2026-02-23): Bitfinex public WS trades can produce duplicate trade events
-- (e.g. te/tu for the same trade_id, plus possible snapshot/live overlap).
-- Fresh DBs now create the unique index above by default.
--
-- For existing live DBs that already contain duplicates, do NOT run the above directly.
-- Use a rollout partial unique index with a deploy cutoff timestamp (see Strategy/Analysis SQL helper)
-- and pair it with repository-side ON CONFLICT DO NOTHING.

-- 5.6 TimescaleDB hypertables za crypto tabele
CREATE EXTENSION IF NOT EXISTS timescaledb;

SELECT create_hypertable(
    'crypto_orderbooks',
    by_range('utc', INTERVAL '1 day'),
    create_default_indexes => FALSE,
    if_not_exists => TRUE
);

-- SELECT create_hypertable('crypto_snapshots', by_range('utc', INTERVAL '1 day'), if_not_exists => TRUE);
-- SELECT create_hypertable('crypto_trades', by_range('utc', INTERVAL '1 day'), if_not_exists => TRUE);
ALTER TABLE crypto_orderbooks SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'exchange, symbol',
    timescaledb.compress_orderby = 'utc DESC, id DESC'
);

-- ALTER TABLE crypto_snapshots SET (timescaledb.compress, timescaledb.compress_segmentby = 'exchange, symbol');
SELECT add_compression_policy(
    'crypto_orderbooks',
    compress_after => INTERVAL '1 day',
    if_not_exists => TRUE
);

SELECT add_retention_policy(
    'crypto_orderbooks',
    drop_after => INTERVAL '14 days',
    if_not_exists => TRUE
);

-- SELECT add_compression_policy('crypto_snapshots', INTERVAL '7 days', if_not_exists => TRUE);
