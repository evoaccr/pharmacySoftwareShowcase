BEGIN;

CREATE TABLE IF NOT EXISTS public.farmassist_invoice_refund_import_map (
    source_refund_fatura_id integer PRIMARY KEY,
    primary_invoice_uuid text NOT NULL
        REFERENCES public.invoices(invoice_uuid),
    target_stock_log_id bigint NOT NULL UNIQUE
        REFERENCES public.invoice_stock_logs(record_id) ON DELETE CASCADE,
    action_code text NOT NULL CHECK (action_code IN ('REFUND', 'PARTIAL_REFUND')),
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp with time zone NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS farmassist_invoice_refund_primary_idx
    ON public.farmassist_invoice_refund_import_map(primary_invoice_uuid);

COMMIT;
