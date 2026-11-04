CREATE TABLE IF NOT EXISTS public.pharmacy_schema_migration (
    migration_id varchar PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.patient
    ADD COLUMN IF NOT EXISTS is_loyal_mechanism3 boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS loyalty_mechanism3_state smallint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS loyalty_mechanism3_score numeric(6,2),
    ADD COLUMN IF NOT EXISTS loyalty_mechanism3_evaluated_at timestamptz,
    ADD COLUMN IF NOT EXISTS loyalty_report_ever_qualified boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS loyalty_report_evidence_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS loyalty_unloyal_since timestamptz;

UPDATE public.patient
   SET loyalty_report_ever_qualified=true
 WHERE is_loyal_mechanism2
   AND NOT loyalty_report_ever_qualified;

ALTER TABLE public.loyal_patient_report_sample_run
    ADD COLUMN IF NOT EXISTS minimum_report_evidence integer NOT NULL DEFAULT 3,
    ADD COLUMN IF NOT EXISTS hybrid_score_threshold numeric(6,2) NOT NULL DEFAULT 60,
    ADD COLUMN IF NOT EXISTS family_loyal_bonus numeric(6,2) NOT NULL DEFAULT 65,
    ADD COLUMN IF NOT EXISTS family_additional_loyal_bonus numeric(6,2) NOT NULL DEFAULT 5,
    ADD COLUMN IF NOT EXISTS family_unloyal_penalty numeric(6,2) NOT NULL DEFAULT 15,
    ADD COLUMN IF NOT EXISTS wear_off_enabled boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS wear_off_days integer NOT NULL DEFAULT 30,
    ADD COLUMN IF NOT EXISTS evidence_source varchar(32) NOT NULL DEFAULT 'MEDICATION_HISTORY';

ALTER TABLE public.loyal_patient_report_supply_evidence
    ADD COLUMN IF NOT EXISTS has_report_evidence boolean,
    ADD COLUMN IF NOT EXISTS is_report_decisive boolean,
    ADD COLUMN IF NOT EXISTS frequency_score numeric(6,2),
    ADD COLUMN IF NOT EXISTS report_score numeric(6,2),
    ADD COLUMN IF NOT EXISTS family_score numeric(6,2),
    ADD COLUMN IF NOT EXISTS hybrid_score numeric(6,2),
    ADD COLUMN IF NOT EXISTS mechanism3_is_loyal boolean,
    ADD COLUMN IF NOT EXISTS loyal_family_member_count integer,
    ADD COLUMN IF NOT EXISTS explicitly_unloyal_family_member_count integer,
    ADD COLUMN IF NOT EXISTS decision_reason text,
    ADD COLUMN IF NOT EXISTS evidence_source varchar(32) NOT NULL DEFAULT 'MEDICATION_HISTORY';

CREATE INDEX IF NOT EXISTS ix_patient_loyal_mechanism3
    ON public.patient(is_loyal_mechanism3) WHERE is_loyal_mechanism3;
CREATE INDEX IF NOT EXISTS ix_patient_loyal_family_state
    ON public.patient(family_group_id, loyalty_mechanism3_state)
    WHERE family_group_id IS NOT NULL;

UPDATE public.patient
   SET is_loyal = is_loyal_mechanism1 OR is_loyal_mechanism2 OR is_loyal_mechanism3
 WHERE is_loyal IS DISTINCT FROM
       (is_loyal_mechanism1 OR is_loyal_mechanism2 OR is_loyal_mechanism3);

INSERT INTO public.pharmacy_schema_migration(migration_id)
VALUES ('069_create_loyal_patient_hybrid_scoring')
ON CONFLICT (migration_id) DO NOTHING;
