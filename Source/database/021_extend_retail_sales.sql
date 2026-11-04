BEGIN;

INSERT INTO public."payementTypes" (record_id, payment_type)
VALUES (6, 'discount')
ON CONFLICT (record_id) DO UPDATE
SET payment_type = EXCLUDED.payment_type;

ALTER TABLE public.sales
    ADD COLUMN IF NOT EXISTS notes varchar,
    ADD COLUMN IF NOT EXISTS with_iskonto boolean NOT NULL DEFAULT FALSE;

CREATE INDEX IF NOT EXISTS ix_sales_timestamp
    ON public.sales ("timestamp" DESC);
CREATE INDEX IF NOT EXISTS ix_sales_user_timestamp
    ON public.sales (user_no, "timestamp" DESC);
CREATE INDEX IF NOT EXISTS ix_qrMovements_reference_code
    ON public."qrMovements" (reference_code);
CREATE INDEX IF NOT EXISTS ix_salesPayements_sale_type
    ON public."salesPayements" (sale_id, payment_type);

COMMIT;
