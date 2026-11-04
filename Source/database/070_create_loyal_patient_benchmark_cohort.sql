CREATE TABLE IF NOT EXISTS public.pharmacy_schema_migration (
    migration_id varchar PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.loyal_patient_benchmark_cohort (
    cohort_key varchar(64) NOT NULL,
    cohort_position integer NOT NULL,
    patient_record_id integer NOT NULL REFERENCES public.patient(record_id) ON DELETE CASCADE,
    selected_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (cohort_key, patient_record_id),
    UNIQUE (cohort_key, cohort_position)
);

-- Preserve the already-tested 300-patient cohort when upgrading an existing
-- installation. Subsequent runs reuse these patient ids instead of resampling.
WITH latest_completed AS (
    SELECT run_id
      FROM public.loyal_patient_report_sample_run
     WHERE status='COMPLETED'
     ORDER BY completed_at DESC NULLS LAST, started_at DESC
     LIMIT 1
), seed AS (
    SELECT evidence.patient_record_id,
           ROW_NUMBER() OVER (
               ORDER BY evidence.evaluated_at, evidence.patient_record_id)::integer AS cohort_position
      FROM public.loyal_patient_report_supply_evidence evidence
      JOIN latest_completed latest ON latest.run_id=evidence.run_id
)
INSERT INTO public.loyal_patient_benchmark_cohort(
    cohort_key, cohort_position, patient_record_id)
SELECT 'LOYALTY_BENCHMARK_V1', cohort_position, patient_record_id
  FROM seed
 WHERE NOT EXISTS (
       SELECT 1 FROM public.loyal_patient_benchmark_cohort
        WHERE cohort_key='LOYALTY_BENCHMARK_V1')
ON CONFLICT DO NOTHING;

ALTER TABLE public.loyal_patient_report_sample_run
    ADD COLUMN IF NOT EXISTS cohort_key varchar(64) NOT NULL DEFAULT 'LOYALTY_BENCHMARK_V1',
    ADD COLUMN IF NOT EXISTS mechanism1_loyal_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS mechanism1_loyal_ratio numeric(7,6),
    ADD COLUMN IF NOT EXISTS mechanism1_estimated_count integer,
    ADD COLUMN IF NOT EXISTS mechanism2_loyal_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS mechanism2_loyal_ratio numeric(7,6),
    ADD COLUMN IF NOT EXISTS mechanism2_estimated_count integer,
    ADD COLUMN IF NOT EXISTS mechanism3_loyal_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS mechanism3_loyal_ratio numeric(7,6),
    ADD COLUMN IF NOT EXISTS mechanism3_estimated_count integer;

ALTER TABLE public.loyal_patient_report_supply_evidence
    ADD COLUMN IF NOT EXISTS mechanism1_is_loyal boolean,
    ADD COLUMN IF NOT EXISTS mechanism2_is_loyal boolean;

INSERT INTO public.pharmacy_schema_migration(migration_id)
VALUES ('070_create_loyal_patient_benchmark_cohort')
ON CONFLICT (migration_id) DO NOTHING;
