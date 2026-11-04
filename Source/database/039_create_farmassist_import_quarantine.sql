BEGIN;

CREATE TABLE IF NOT EXISTS public.farmassist_import_quarantine (
    source_table varchar NOT NULL,
    source_transaction_id integer NOT NULL,
    source_row_id integer NOT NULL,
    source_kind varchar NOT NULL,
    reason varchar NOT NULL,
    source_payload jsonb NOT NULL,
    resolved boolean NOT NULL DEFAULT FALSE,
    quarantined_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source_table, source_row_id, reason),
    CONSTRAINT ck_farmassist_import_quarantine_kind
        CHECK (source_kind IN ('sale', 'refund'))
);

CREATE INDEX IF NOT EXISTS ix_farmassist_import_quarantine_unresolved
    ON public.farmassist_import_quarantine (source_transaction_id, source_row_id)
    WHERE resolved=FALSE;

COMMIT;
