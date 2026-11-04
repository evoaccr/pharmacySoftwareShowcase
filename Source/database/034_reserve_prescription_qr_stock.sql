BEGIN;

ALTER TABLE public."qrActive"
    ADD COLUMN IF NOT EXISTS reserved_prescription_uid uuid,
    ADD COLUMN IF NOT EXISTS reserved_at timestamptz;

CREATE INDEX IF NOT EXISTS ix_qrActive_reserved_prescription
    ON public."qrActive" (reserved_prescription_uid)
    WHERE reserved_prescription_uid IS NOT NULL;

COMMIT;
