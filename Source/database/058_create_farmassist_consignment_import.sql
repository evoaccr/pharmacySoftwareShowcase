BEGIN;

ALTER TABLE public.farmassist_import_quarantine
    DROP CONSTRAINT IF EXISTS ck_farmassist_import_quarantine_kind;

ALTER TABLE public.farmassist_import_quarantine
    ADD CONSTRAINT ck_farmassist_import_quarantine_kind
        CHECK (source_kind IN ('sale', 'refund', 'takas', 'consignment'));

CREATE TABLE IF NOT EXISTS public.farmassist_consignment_import_map (
    source_emanet_id integer PRIMARY KEY,
    target_consignment_id integer NOT NULL
        REFERENCES public."consignmentSales"(record_id) ON DELETE CASCADE,
    source_group_key char(64) NOT NULL,
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS farmassist_consignment_target_idx
    ON public.farmassist_consignment_import_map(target_consignment_id);

CREATE INDEX IF NOT EXISTS farmassist_consignment_group_idx
    ON public.farmassist_consignment_import_map(source_group_key);

COMMIT;
