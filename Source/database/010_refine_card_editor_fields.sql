BEGIN;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'patient'
          AND column_name = 'registration_no'
    ) AND NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'patient'
          AND column_name = 'sicil_no'
    ) THEN
        ALTER TABLE public.patient RENAME COLUMN registration_no TO sicil_no;
    END IF;
END
$$;

ALTER TABLE public.patient
    ALTER COLUMN kurum_id TYPE varchar USING kurum_id::varchar,
    ALTER COLUMN allow_debt SET DEFAULT TRUE,
    ALTER COLUMN debt_amount SET DEFAULT 0,
    ALTER COLUMN maximum_debt SET DEFAULT 1500,
    ALTER COLUMN debt_credit_score SET DEFAULT 0;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'patient_kurum_id_fkey'
          AND conrelid = 'public.patient'::regclass
    ) THEN
        ALTER TABLE public.patient
            ADD CONSTRAINT patient_kurum_id_fkey
            FOREIGN KEY (kurum_id)
            REFERENCES public.kurum (kurum_code);
    END IF;
END
$$;

COMMIT;
