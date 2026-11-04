CREATE TABLE IF NOT EXISTS public.pharmacy_schema_migration (
    migration_id varchar PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.patient
    ADD COLUMN IF NOT EXISTS is_loyal_mechanism1 boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS is_loyal_mechanism2 boolean NOT NULL DEFAULT false;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM public.pharmacy_schema_migration
         WHERE migration_id='068_create_loyal_patient_report_sampling') THEN
        UPDATE public.patient SET is_loyal_mechanism1 = is_loyal;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS public.loyal_patient_report_sample_run (
    run_id uuid PRIMARY KEY,
    started_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    configured_sample_size integer NOT NULL,
    actual_sample_size integer NOT NULL DEFAULT 0,
    threshold_ratio numeric(7,6) NOT NULL,
    delay_seconds numeric(8,3) NOT NULL,
    jitter_seconds numeric(8,3) NOT NULL,
    total_patient_count integer NOT NULL DEFAULT 0,
    evaluated_patient_count integer NOT NULL DEFAULT 0,
    failed_patient_count integer NOT NULL DEFAULT 0,
    patient_with_report_count integer NOT NULL DEFAULT 0,
    loyal_sample_count integer NOT NULL DEFAULT 0,
    sample_loyal_ratio numeric(7,6),
    estimated_loyal_patient_count integer,
    status varchar(24) NOT NULL DEFAULT 'RUNNING',
    error text
);

CREATE TABLE IF NOT EXISTS public.loyal_patient_report_supply_evidence (
    run_id uuid NOT NULL REFERENCES public.loyal_patient_report_sample_run(run_id) ON DELETE CASCADE,
    patient_record_id integer NOT NULL REFERENCES public.patient(record_id) ON DELETE CASCADE,
    patient_tc_identity_no varchar(11) NOT NULL,
    total_report_count integer,
    supplied_report_count integer,
    supply_ratio numeric(7,6),
    is_loyal boolean,
    evaluated_at timestamptz NOT NULL DEFAULT now(),
    error text,
    PRIMARY KEY (run_id, patient_record_id)
);

CREATE INDEX IF NOT EXISTS ix_loyal_report_evidence_patient
    ON public.loyal_patient_report_supply_evidence(patient_record_id, evaluated_at DESC);
CREATE INDEX IF NOT EXISTS ix_patient_loyal_mechanism2
    ON public.patient(is_loyal_mechanism2) WHERE is_loyal_mechanism2;

UPDATE public.patient
   SET is_loyal = is_loyal_mechanism1 OR is_loyal_mechanism2
 WHERE is_loyal IS DISTINCT FROM (is_loyal_mechanism1 OR is_loyal_mechanism2);

INSERT INTO public.pharmacy_schema_migration(migration_id)
VALUES ('068_create_loyal_patient_report_sampling')
ON CONFLICT (migration_id) DO NOTHING;
