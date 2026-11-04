CREATE TABLE IF NOT EXISTS public.pharmacy_schema_migration (
    migration_id varchar PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.patient
    ADD COLUMN IF NOT EXISTS loyalty_unloyal_strikes integer NOT NULL DEFAULT 0;

UPDATE public.patient
   SET loyalty_unloyal_strikes=GREATEST(0, LEAST(3, loyalty_unloyal_strikes));

ALTER TABLE public.patient
    DROP CONSTRAINT IF EXISTS patient_loyalty_unloyal_strikes_check;
ALTER TABLE public.patient
    ADD CONSTRAINT patient_loyalty_unloyal_strikes_check
    CHECK (loyalty_unloyal_strikes BETWEEN 0 AND 3);

INSERT INTO public.pharmacy_schema_migration(migration_id)
VALUES ('073_add_loyal_patient_strikes')
ON CONFLICT (migration_id) DO NOTHING;
