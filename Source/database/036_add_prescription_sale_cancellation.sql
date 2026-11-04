BEGIN;

ALTER TABLE public.sales
    ADD COLUMN IF NOT EXISTS cancelled_at timestamp with time zone;

ALTER TABLE public."salesPayements"
    ADD COLUMN IF NOT EXISTS cancelled_at timestamp with time zone;

ALTER TABLE public."salesRefunds"
    ADD COLUMN IF NOT EXISTS original_sale_id integer REFERENCES public.sales(record_id),
    ADD COLUMN IF NOT EXISTS prescription_uid uuid,
    ADD COLUMN IF NOT EXISTS refund_source varchar NOT NULL DEFAULT 'retail',
    ADD COLUMN IF NOT EXISTS refund_status varchar NOT NULL DEFAULT 'completed',
    ADD COLUMN IF NOT EXISTS patient_amount numeric(12,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS institution_amount numeric(12,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS institution_secondary_amount numeric(12,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS owed_amount numeric(12,2) NOT NULL DEFAULT 0;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_salesRefunds_source') THEN
        ALTER TABLE public."salesRefunds"
            ADD CONSTRAINT "ck_salesRefunds_source"
            CHECK (refund_source IN ('retail', 'prescription'));
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_salesRefunds_status') THEN
        ALTER TABLE public."salesRefunds"
            ADD CONSTRAINT "ck_salesRefunds_status"
            CHECK (refund_status IN ('completed', 'reconciliation_required', 'cancelled'));
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_salesRefunds_prescription_amounts') THEN
        ALTER TABLE public."salesRefunds"
            ADD CONSTRAINT "ck_salesRefunds_prescription_amounts"
            CHECK (patient_amount >= 0
                AND institution_amount >= 0
                AND institution_secondary_amount >= 0
                AND owed_amount >= 0);
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_salesRefunds_prescription_sale
    ON public."salesRefunds" (original_sale_id)
    WHERE refund_source='prescription';
CREATE INDEX IF NOT EXISTS ix_salesRefunds_prescription_uid
    ON public."salesRefunds" (prescription_uid)
    WHERE prescription_uid IS NOT NULL;

ALTER TABLE public."salesRefundPayements"
    ADD COLUMN IF NOT EXISTS payment_role varchar,
    ADD COLUMN IF NOT EXISTS payment_finished boolean NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS settled_at timestamp with time zone;

ALTER TABLE public."salesRefundPayements"
    DROP CONSTRAINT IF EXISTS "ck_salesRefundPayements_supported_type";
ALTER TABLE public."salesRefundPayements"
    DROP CONSTRAINT IF EXISTS ck_salesrefundpayements_supported_type;
ALTER TABLE public."salesRefundPayements"
    ADD CONSTRAINT ck_salesrefundpayements_supported_type
    CHECK (payment_type IN (1, 2, 3, 7));

CREATE UNIQUE INDEX IF NOT EXISTS ux_salesRefundPayements_prescription_role
    ON public."salesRefundPayements" (refund_id, payment_role)
    WHERE payment_role='patient';

ALTER TABLE public."salesRefundItems"
    ADD COLUMN IF NOT EXISTS item_source varchar NOT NULL DEFAULT 'retail',
    ADD COLUMN IF NOT EXISTS inventory_tracked boolean NOT NULL DEFAULT TRUE;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_salesRefundItems_item_source') THEN
        ALTER TABLE public."salesRefundItems"
            ADD CONSTRAINT "ck_salesRefundItems_item_source"
            CHECK (item_source IN ('retail', 'prescription'));
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_salesRefundItems_refund_qr
    ON public."salesRefundItems" (refund_id, qr_code)
    WHERE item_source='prescription';

ALTER TABLE public.prescription_sale_operations
    DROP CONSTRAINT IF EXISTS ck_prescription_sale_operation_type;
ALTER TABLE public.prescription_sale_operations
    ADD CONSTRAINT ck_prescription_sale_operation_type
    CHECK (operation_type IN (
        'ITS_SALE', 'QR_FINALIZE', 'QR_FINALIZE_CANCEL', 'ITS_SALE_CANCEL'));

CREATE OR REPLACE FUNCTION public.pharmacy_refresh_refund_payment_status(target_refund_id integer)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    refund_due numeric(12,2);
    paid_total numeric(12,2);
BEGIN
    SELECT CASE
            WHEN refund_source='prescription' THEN patient_amount
            ELSE GREATEST(total_amount - debt_reduction_amount, 0)
        END
    INTO refund_due
    FROM public."salesRefunds"
    WHERE record_id = target_refund_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN;
    END IF;

    SELECT COALESCE(SUM(amount) FILTER (WHERE payment_finished), 0)
    INTO paid_total
    FROM public."salesRefundPayements"
    WHERE refund_id = target_refund_id;

    UPDATE public."salesRefunds"
    SET payement_finished = paid_total = refund_due
    WHERE record_id = target_refund_id;
END;
$$;

COMMIT;
