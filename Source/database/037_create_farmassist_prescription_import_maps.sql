BEGIN;

CREATE TABLE IF NOT EXISTS public.farmassist_prescription_import_map (
    source_prescription_id integer PRIMARY KEY,
    target_prescription_id integer NOT NULL UNIQUE
        REFERENCES public.prescription (record_id) ON DELETE CASCADE,
    prescription_uid uuid NOT NULL UNIQUE,
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS public.farmassist_prescription_sale_import_map (
    source_sale_id integer PRIMARY KEY,
    source_prescription_id integer NOT NULL,
    target_sale_id integer NOT NULL UNIQUE
        REFERENCES public.sales (record_id) ON DELETE CASCADE,
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS public.farmassist_prescription_refund_import_map (
    source_sale_id integer PRIMARY KEY,
    source_original_sale_id integer NOT NULL,
    target_refund_id integer NOT NULL UNIQUE
        REFERENCES public."salesRefunds" (record_id) ON DELETE CASCADE,
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

COMMIT;
