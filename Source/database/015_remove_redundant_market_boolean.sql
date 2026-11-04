BEGIN;

ALTER TABLE public."barcodeNew"
    DROP COLUMN IF EXISTS is_in_turkish_market;

COMMIT;
