BEGIN;

ALTER TABLE public.doctor
    ADD COLUMN IF NOT EXISTS phone_numbers varchar[],
    ADD COLUMN IF NOT EXISTS email_addresses varchar[];

UPDATE public.doctor
SET phone_numbers = ARRAY[phone_number]
WHERE phone_numbers IS NULL
  AND NULLIF(BTRIM(phone_number), '') IS NOT NULL;

UPDATE public.doctor
SET email_addresses = ARRAY[email_address]
WHERE email_addresses IS NULL
  AND NULLIF(BTRIM(email_address), '') IS NOT NULL;

ALTER TABLE public.faculty
    ADD COLUMN IF NOT EXISTS email_addresses varchar[];

COMMIT;
