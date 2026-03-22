BEGIN;

ALTER TABLE funding_interest_ledger
    ADD COLUMN IF NOT EXISTS ledger_id BIGINT;

ALTER TABLE funding_interest_ledger
    ADD COLUMN IF NOT EXISTS wallet_type TEXT;

ALTER TABLE funding_interest_ledger
    ADD COLUMN IF NOT EXISTS raw_amount NUMERIC(18,12);

ALTER TABLE funding_interest_ledger
    ADD COLUMN IF NOT EXISTS balance_after NUMERIC(18,12);

ALTER TABLE funding_interest_ledger
    ADD COLUMN IF NOT EXISTS description TEXT;

UPDATE funding_interest_ledger
SET
    wallet_type = COALESCE(wallet_type, 'funding'),
    raw_amount = COALESCE(raw_amount, net_interest),
    balance_after = balance_after,
    description = COALESCE(description, entry_type)
WHERE
    wallet_type IS NULL OR
    raw_amount IS NULL OR
    description IS NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ux_funding_interest_ledger_exchange_ledger_id'
    ) THEN
        ALTER TABLE funding_interest_ledger
            ADD CONSTRAINT ux_funding_interest_ledger_exchange_ledger_id
            UNIQUE (exchange, ledger_id);
    END IF;
END $$;

COMMIT;
