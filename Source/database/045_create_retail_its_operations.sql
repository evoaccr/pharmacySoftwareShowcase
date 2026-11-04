BEGIN;

CREATE TABLE IF NOT EXISTS public.retail_its_operations (
    operation_id uuid PRIMARY KEY,
    operation_type varchar NOT NULL,
    operation_status varchar NOT NULL,
    pharmacy_gln varchar NOT NULL,
    request_payload jsonb NOT NULL,
    qr_codes varchar[] NOT NULL DEFAULT ARRAY[]::varchar[],
    requested_by varchar NOT NULL,
    prepared_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    remote_confirmed_at timestamp with time zone,
    local_completed_at timestamp with time zone,
    notification_id varchar,
    http_status integer,
    response_body text,
    fault_code varchar,
    fault_message text,
    error_text text,
    sale_id integer REFERENCES public.sales(record_id),
    refund_id integer REFERENCES public."salesRefunds"(record_id),
    CONSTRAINT ck_retail_its_operation_type
        CHECK (operation_type IN ('SALE', 'SALE_CANCELLATION')),
    CONSTRAINT ck_retail_its_operation_status
        CHECK (operation_status IN (
            'prepared', 'remote_confirmed', 'local_completed', 'definitely_not_sent',
            'failed', 'reconciliation_required')),
    CONSTRAINT ck_retail_its_operation_reference
        CHECK (
            (operation_type='SALE' AND refund_id IS NULL) OR
            (operation_type='SALE_CANCELLATION' AND sale_id IS NULL))
);

CREATE TABLE IF NOT EXISTS public.retail_its_operation_items (
    operation_id uuid NOT NULL
        REFERENCES public.retail_its_operations(operation_id) ON DELETE CASCADE,
    qr_code varchar NOT NULL,
    is_active boolean NOT NULL DEFAULT TRUE,
    PRIMARY KEY (operation_id, qr_code)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_retail_its_operation_items_active_qr
    ON public.retail_its_operation_items (qr_code)
    WHERE is_active;

CREATE INDEX IF NOT EXISTS ix_retail_its_operations_status_time
    ON public.retail_its_operations (operation_status, prepared_at DESC);

CREATE INDEX IF NOT EXISTS ix_retail_its_operations_sale
    ON public.retail_its_operations (sale_id)
    WHERE sale_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_retail_its_operations_refund
    ON public.retail_its_operations (refund_id)
    WHERE refund_id IS NOT NULL;

COMMIT;
