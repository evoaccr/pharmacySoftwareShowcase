BEGIN;

CREATE TABLE IF NOT EXISTS public.farmassist_invoice_stock_import_map (
    source_fatura_id integer PRIMARY KEY,
    target_invoice_uuid text NOT NULL
        REFERENCES public.invoices(invoice_uuid) ON DELETE CASCADE,
    target_stock_log_id bigint NOT NULL UNIQUE
        REFERENCES public.invoice_stock_logs(record_id) ON DELETE CASCADE,
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp with time zone NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS farmassist_invoice_stock_import_target_idx
    ON public.farmassist_invoice_stock_import_map(target_invoice_uuid);

COMMIT;
