BEGIN;

INSERT INTO public."payementTypes" (record_id, payment_type) VALUES
    (7, 'prescription_patient'),
    (8, 'institution_secondary')
ON CONFLICT (record_id) DO UPDATE
SET payment_type = EXCLUDED.payment_type;

ALTER TABLE public.sales
    ADD COLUMN IF NOT EXISTS prescription_uid uuid,
    ADD COLUMN IF NOT EXISTS sale_status varchar NOT NULL DEFAULT 'completed',
    ADD COLUMN IF NOT EXISTS patient_amount numeric(12,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS institution_secondary_amount numeric(12,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS owed_amount numeric(12,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS medula_sale_confirmed_at timestamp with time zone,
    ADD COLUMN IF NOT EXISTS prescription_finalized_at timestamp with time zone;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_sales_sale_status') THEN
        ALTER TABLE public.sales
            ADD CONSTRAINT ck_sales_sale_status
            CHECK (sale_status IN (
                'pending', 'medula_reported', 'completed', 'reconciliation_required', 'cancelled'));
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_sales_prescription_amounts') THEN
        ALTER TABLE public.sales
            ADD CONSTRAINT ck_sales_prescription_amounts
            CHECK (patient_amount >= 0
                AND institution_secondary_amount >= 0
                AND owed_amount >= 0);
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_prescription_uid
    ON public.sales (prescription_uid)
    WHERE prescription_uid IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_sales_status_timestamp
    ON public.sales (sale_status, "timestamp" DESC);

ALTER TABLE public."salesPayements"
    ADD COLUMN IF NOT EXISTS payment_role varchar,
    ADD COLUMN IF NOT EXISTS owed_amount numeric(12,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS payment_finished boolean NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS settled_at timestamp with time zone,
    ADD COLUMN IF NOT EXISTS invoice_no varchar;

UPDATE public."salesPayements" payment
SET payment_role = CASE type.payment_type
        WHEN 'institution' THEN 'institution_primary'
        WHEN 'institution_secondary' THEN 'institution_secondary'
        WHEN 'prescription_patient' THEN 'patient'
        ELSE type.payment_type
    END,
    settled_at = CASE
        WHEN payment.payment_finished THEN COALESCE(payment.settled_at, CURRENT_TIMESTAMP)
        ELSE payment.settled_at
    END
FROM public."payementTypes" type
WHERE type.record_id = payment.payment_type
  AND NULLIF(BTRIM(payment.payment_role), '') IS NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_salesPayements_owed_amount') THEN
        ALTER TABLE public."salesPayements"
            ADD CONSTRAINT "ck_salesPayements_owed_amount"
            CHECK (owed_amount >= 0);
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_salesPayements_prescription_role
    ON public."salesPayements" (sale_id, payment_role)
    WHERE payment_role IN ('patient', 'institution_primary', 'institution_secondary');

-- Legacy retail rows intentionally repeat a barcode once per sold unit, so a
-- global sale/QR uniqueness rule would corrupt their quantity representation.
-- Prescription rows contain serialized ITS codes and are unique within a sale.
ALTER TABLE public."salesQrItems"
    ADD COLUMN IF NOT EXISTS item_source varchar NOT NULL DEFAULT 'retail',
    ADD COLUMN IF NOT EXISTS inventory_tracked boolean NOT NULL DEFAULT TRUE;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_salesQrItems_item_source') THEN
        ALTER TABLE public."salesQrItems"
            ADD CONSTRAINT "ck_salesQrItems_item_source"
            CHECK (item_source IN ('retail', 'prescription'));
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_salesQrItems_sale_qr
    ON public."salesQrItems" (sale_id, qr_code)
    WHERE item_source='prescription';

CREATE TABLE IF NOT EXISTS public.prescription_sale_operations (
    operation_id uuid PRIMARY KEY,
    prescription_uid uuid NOT NULL,
    sale_id integer NOT NULL REFERENCES public.sales(record_id),
    operation_type varchar NOT NULL,
    operation_status varchar NOT NULL,
    qr_codes varchar[] NOT NULL DEFAULT ARRAY[]::varchar[],
    untracked_qr_codes varchar[] NOT NULL DEFAULT ARRAY[]::varchar[],
    requested_by varchar REFERENCES public.users(user_id),
    prepared_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    remote_confirmed_at timestamp with time zone,
    local_completed_at timestamp with time zone,
    remote_message text,
    error_text text,
    CONSTRAINT ck_prescription_sale_operation_type
        CHECK (operation_type IN ('ITS_SALE', 'QR_FINALIZE')),
    CONSTRAINT ck_prescription_sale_operation_status
        CHECK (operation_status IN (
            'prepared', 'remote_confirmed', 'local_completed', 'failed', 'cancelled',
            'reconciliation_required'))
);

ALTER TABLE public.prescription_sale_operations
    ADD COLUMN IF NOT EXISTS untracked_qr_codes varchar[] NOT NULL DEFAULT ARRAY[]::varchar[];

CREATE INDEX IF NOT EXISTS ix_prescription_sale_operations_uid_time
    ON public.prescription_sale_operations (prescription_uid, prepared_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_prescription_sale_operations_active
    ON public.prescription_sale_operations (prescription_uid, operation_type)
    WHERE operation_status IN ('prepared', 'remote_confirmed', 'reconciliation_required');

CREATE OR REPLACE FUNCTION public.pharmacy_refresh_sale_payment_status(target_sale_id integer)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    sale_total numeric(12,2);
    institution_due numeric(12,2);
    customer_due numeric(12,2);
    customer_paid numeric(12,2);
    institution_paid numeric(12,2);
BEGIN
    SELECT total_amount, institution_amount
    INTO sale_total, institution_due
    FROM public.sales
    WHERE record_id = target_sale_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN;
    END IF;

    customer_due := GREATEST(sale_total - institution_due, 0);

    SELECT
        COALESCE(SUM(payment.amount) FILTER (
            WHERE type.payment_type NOT IN ('institution', 'institution_secondary')
              AND payment.payment_finished), 0),
        COALESCE(SUM(payment.amount) FILTER (
            WHERE type.payment_type IN ('institution', 'institution_secondary')
              AND payment.payment_finished), 0)
    INTO customer_paid, institution_paid
    FROM public."salesPayements" payment
    JOIN public."payementTypes" type ON type.record_id = payment.payment_type
    WHERE payment.sale_id = target_sale_id;

    UPDATE public.sales
    SET payement_finished = customer_paid >= customer_due,
        institution_payement_finished = institution_paid >= institution_due
    WHERE record_id = target_sale_id;
END;
$$;

COMMIT;
