BEGIN;

ALTER TABLE public.farmassist_import_quarantine
    DROP CONSTRAINT IF EXISTS ck_farmassist_import_quarantine_kind;

ALTER TABLE public.farmassist_import_quarantine
    ADD CONSTRAINT ck_farmassist_import_quarantine_kind
        CHECK (source_kind IN (
            'sale', 'refund', 'takas', 'consignment', 'manual_stock',
            'family_group', 'patient_debt'
        ));

CREATE TABLE IF NOT EXISTS public.farmassist_family_group_import_map (
    source_group_id integer PRIMARY KEY,
    target_family_group_id integer NOT NULL
        REFERENCES public.family_group(record_id) ON DELETE CASCADE,
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_farmassist_family_group_import_target
    ON public.farmassist_family_group_import_map(target_family_group_id);

CREATE TABLE IF NOT EXISTS public.farmassist_patient_debt_import_map (
    source_table varchar NOT NULL
        CHECK (source_table IN ('dbo.Veresiye', 'dbo.VeresiyeOdenen')),
    source_id integer NOT NULL,
    target_log_id bigint REFERENCES public.patient_debt_log(log_id) ON DELETE CASCADE,
    import_action varchar NOT NULL
        CHECK (import_action IN ('imported', 'ignored_no_effect')),
    source_fingerprint char(64) NOT NULL,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source_table, source_id),
    UNIQUE (target_log_id),
    CONSTRAINT ck_farmassist_patient_debt_import_target
        CHECK (
            (import_action='imported' AND target_log_id IS NOT NULL)
            OR (import_action='ignored_no_effect' AND target_log_id IS NULL)
        )
);

COMMIT;
