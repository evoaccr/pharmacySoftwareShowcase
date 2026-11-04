BEGIN;

CREATE TABLE IF NOT EXISTS public.farmassist_retail_sale_import_map (
    source_sale_id integer PRIMARY KEY,
    target_sale_id integer NOT NULL UNIQUE
        REFERENCES public.sales (record_id) ON DELETE CASCADE,
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS public.farmassist_retail_refund_import_map (
    source_sale_id integer PRIMARY KEY,
    target_refund_id integer NOT NULL UNIQUE
        REFERENCES public."salesRefunds" (record_id) ON DELETE CASCADE,
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

COMMIT;
