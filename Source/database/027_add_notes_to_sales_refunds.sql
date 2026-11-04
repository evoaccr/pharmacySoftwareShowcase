BEGIN;

-- public.sales already has a notes column (added in 021_extend_retail_sales.sql)
-- but it was never wired up in the UI/repository until now. Add the matching
-- column to salesRefunds so both sale and return screens can store a note.
ALTER TABLE public."salesRefunds"
    ADD COLUMN IF NOT EXISTS notes varchar;

COMMIT;
