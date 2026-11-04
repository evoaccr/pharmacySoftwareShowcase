BEGIN;

ALTER TABLE public.sales
    ADD COLUMN IF NOT EXISTS marked boolean NOT NULL DEFAULT FALSE;

ALTER TABLE public."salesRefunds"
    ADD COLUMN IF NOT EXISTS marked boolean NOT NULL DEFAULT FALSE;

-- Retail/prescription return reference codes used the 'R' prefix, which is being
-- renamed to 'G' (Geri İade) to avoid ambiguity. Rewrite historical qrMovements rows
-- so past returns resolve under the new prefix the same way future ones will.
UPDATE public."qrMovements"
SET reference_code = 'G' || SUBSTRING(reference_code FROM 2)
WHERE reference_code ~ '^R[0-9]+$';

COMMIT;
