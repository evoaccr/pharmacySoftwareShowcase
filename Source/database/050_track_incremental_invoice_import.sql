BEGIN;

CREATE TABLE IF NOT EXISTS public.invoice_incremental_sync_state (
    source_key text PRIMARY KEY,
    last_successful_cutoff timestamp with time zone,
    active_run_id uuid,
    active_run_cutoff timestamp with time zone,
    phase text NOT NULL DEFAULT 'Idle',
    total integer NOT NULL DEFAULT 0,
    completed integer NOT NULL DEFAULT 0,
    imported integer NOT NULL DEFAULT 0,
    failed integer NOT NULL DEFAULT 0,
    current_invoice text NOT NULL DEFAULT '',
    last_error text NOT NULL DEFAULT '',
    updated_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT invoice_incremental_sync_phase_check CHECK (
        phase IN ('Idle', 'Discovering', 'Running', 'Paused', 'Faulted', 'Completed'))
);

CREATE TABLE IF NOT EXISTS public.invoice_incremental_sync_items (
    run_id uuid NOT NULL,
    type_key text NOT NULL,
    document_uuid text NOT NULL,
    document_number text NOT NULL DEFAULT '',
    issue_date date,
    received_at timestamp with time zone,
    status text NOT NULL DEFAULT 'Pending',
    last_error text NOT NULL DEFAULT '',
    updated_at timestamp with time zone NOT NULL DEFAULT now(),
    PRIMARY KEY (run_id, type_key, document_uuid),
    CONSTRAINT invoice_incremental_sync_item_status_check CHECK (
        status IN ('Pending', 'Imported', 'Failed'))
);

CREATE INDEX IF NOT EXISTS invoice_incremental_sync_items_pending_idx
    ON public.invoice_incremental_sync_items (run_id, status, received_at, issue_date);

COMMENT ON TABLE public.invoice_incremental_sync_state IS
    'Durable Eczaci Kart incremental XML import watermark and active-run summary.';
COMMENT ON TABLE public.invoice_incremental_sync_items IS
    'Per-invoice checkpoint rows for resumable Eczaci Kart incremental XML imports.';

COMMIT;
