BEGIN;

ALTER TABLE public.farmassist_import_quarantine
    DROP CONSTRAINT IF EXISTS ck_farmassist_import_quarantine_kind;

ALTER TABLE public.farmassist_import_quarantine
    ADD CONSTRAINT ck_farmassist_import_quarantine_kind
        CHECK (source_kind IN ('sale', 'refund', 'takas'));

COMMIT;
