-- Relax crypto_snapshots.snapshot_type validation so new feature-specific
-- snapshot categories (for example funding_*) can be written without a schema
-- change on every new subtype.

BEGIN;

ALTER TABLE crypto_snapshots
    DROP CONSTRAINT IF EXISTS crypto_snapshots_snapshot_type_check;

ALTER TABLE crypto_snapshots
    ADD CONSTRAINT crypto_snapshots_snapshot_type_check
    CHECK (snapshot_type ~ '^[a-z][a-z0-9_]*$');

COMMIT;
