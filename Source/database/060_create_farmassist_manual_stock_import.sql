BEGIN;

ALTER TABLE public.farmassist_import_quarantine
    DROP CONSTRAINT IF EXISTS ck_farmassist_import_quarantine_kind;

ALTER TABLE public.farmassist_import_quarantine
    ADD CONSTRAINT ck_farmassist_import_quarantine_kind
        CHECK (source_kind IN ('sale', 'refund', 'takas', 'consignment', 'manual_stock'));

CREATE TABLE IF NOT EXISTS public.farmassist_manual_movement_import_map (
    source_movement_type smallint NOT NULL
        CHECK (source_movement_type IN (9, 10, 23, 24)),
    source_transaction_id integer NOT NULL,
    target_manual_movement_id integer NOT NULL
        REFERENCES public.manual_movements(record_id) ON DELETE CASCADE,
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source_movement_type, source_transaction_id),
    UNIQUE (target_manual_movement_id)
);

COMMIT;
