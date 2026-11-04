BEGIN;

ALTER TABLE public."barcodeNew"
    DROP COLUMN IF EXISTS warning_icons,
    ADD COLUMN IF NOT EXISTS medula_details jsonb,
    ADD COLUMN IF NOT EXISTS medula_info jsonb;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'barcodeNew'
          AND column_name = 'photo'
          AND udt_name = 'bytea'
    ) THEN
        ALTER TABLE public."barcodeNew"
            ALTER COLUMN photo TYPE bytea[]
            USING CASE WHEN photo IS NULL THEN NULL ELSE ARRAY[photo] END;
    END IF;
END $$;

COMMIT;
