BEGIN;

DROP FUNCTION IF EXISTS public.pharmacy_qr_lot(varchar);

ALTER TABLE IF EXISTS public."qrActive"
    DROP COLUMN IF EXISTS lot_number;

ALTER TABLE IF EXISTS public."qrInactive"
    DROP COLUMN IF EXISTS lot_number;

COMMIT;
