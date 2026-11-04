BEGIN;

ALTER TABLE public.users
    ADD COLUMN IF NOT EXISTS password varchar,
    ADD COLUMN IF NOT EXISTS session_active boolean;

ALTER TABLE public.users
    ALTER COLUMN session_active SET DEFAULT false;

UPDATE public.users
SET session_active = false
WHERE session_active IS NULL;

ALTER TABLE public.users
    ALTER COLUMN session_active SET NOT NULL;

COMMIT;
