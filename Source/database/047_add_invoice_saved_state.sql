BEGIN;

ALTER TABLE public.invoices
    ADD COLUMN IF NOT EXISTS is_saved boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN public.invoices.is_saved IS
    'True when the invoice has been finalized into stock/movement history.';

CREATE TABLE IF NOT EXISTS public.farmassist_invoice_import_map (
    source_fatura_id integer PRIMARY KEY,
    target_invoice_uuid text NOT NULL REFERENCES public.invoices(invoice_uuid) ON DELETE CASCADE,
    source_fingerprint character(64) NOT NULL,
    match_method text NOT NULL,
    imported_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT farmassist_invoice_import_map_method_check CHECK (
        match_method IN (
            'source-map', 'ettn', 'header', 'header-relaxed',
            'line-signature', 'party', 'inserted', 'released-old-saved'))
);

CREATE UNIQUE INDEX IF NOT EXISTS farmassist_invoice_import_map_target_idx
    ON public.farmassist_invoice_import_map(target_invoice_uuid);

COMMIT;
