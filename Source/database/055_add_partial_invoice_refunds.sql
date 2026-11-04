BEGIN;
SET LOCAL lock_timeout = '5s';

ALTER TABLE public."qrMovements"
    ADD COLUMN IF NOT EXISTS notes text NOT NULL DEFAULT '';

CREATE TABLE IF NOT EXISTS public.invoice_stock_log_invoices (
    invoice_stock_log_id bigint NOT NULL
        REFERENCES public.invoice_stock_logs(record_id) ON DELETE CASCADE,
    invoice_uuid text NOT NULL
        REFERENCES public.invoices(invoice_uuid),
    PRIMARY KEY (invoice_stock_log_id, invoice_uuid)
);

INSERT INTO public.invoice_stock_log_invoices (invoice_stock_log_id, invoice_uuid)
SELECT record_id, invoice_uuid
FROM public.invoice_stock_logs
ON CONFLICT DO NOTHING;

ALTER TABLE public.invoice_stock_logs
    DROP CONSTRAINT IF EXISTS invoice_stock_logs_action_check;
ALTER TABLE public.invoice_stock_logs
    ADD CONSTRAINT invoice_stock_logs_action_check
    CHECK (action_code IN ('SAVE', 'REFUND', 'PARTIAL_REFUND'));

CREATE INDEX IF NOT EXISTS invoice_stock_log_invoices_invoice_idx
    ON public.invoice_stock_log_invoices(invoice_uuid, invoice_stock_log_id);
CREATE INDEX IF NOT EXISTS invoice_line_qr_codes_qr_idx
    ON public.invoice_line_qr_codes(qr_code);

COMMIT;
