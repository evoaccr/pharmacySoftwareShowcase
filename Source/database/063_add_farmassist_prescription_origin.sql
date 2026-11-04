BEGIN;

ALTER TABLE public.prescription
    ADD COLUMN IF NOT EXISTS imported_from_farmassist boolean NOT NULL DEFAULT false;

ALTER TABLE public.farmassist_prescription_import_map
    DROP CONSTRAINT IF EXISTS farmassist_prescription_import_map_target_prescription_id_key;

CREATE INDEX IF NOT EXISTS ix_farmassist_prescription_import_map_target
    ON public.farmassist_prescription_import_map (target_prescription_id);

CREATE TABLE IF NOT EXISTS public.farmassist_e_prescription_list_import_map (
    source_e_prescription_list_id integer PRIMARY KEY,
    target_prescription_id integer NOT NULL
        REFERENCES public.prescription (record_id) ON DELETE CASCADE,
    prescription_uid uuid NOT NULL,
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_farmassist_e_prescription_list_map_target
    ON public.farmassist_e_prescription_list_import_map (target_prescription_id);

COMMIT;
