BEGIN;

ALTER TABLE funding_offers
    ADD COLUMN IF NOT EXISTS managed_by_engine BOOLEAN NOT NULL DEFAULT FALSE;

UPDATE funding_offers fo
SET managed_by_engine = TRUE
WHERE fo.is_active = TRUE
  AND EXISTS (
      SELECT 1
      FROM funding_offer_events e
      WHERE e.exchange = fo.exchange
        AND e.offer_id = fo.offer_id
        AND e.event_type = 'action_result_submit_offer'
        AND COALESCE((e.metadata ->> 'Success')::BOOLEAN, FALSE) = TRUE
  );

COMMIT;
