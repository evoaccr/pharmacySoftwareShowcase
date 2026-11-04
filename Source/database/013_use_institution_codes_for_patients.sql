BEGIN;

ALTER TABLE public.patient
    DROP CONSTRAINT IF EXISTS patient_kurum_id_fkey;

DO $$
DECLARE
    current_type text;
BEGIN
    SELECT data_type
    INTO current_type
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'patient'
      AND column_name = 'kurum_id';

    IF current_type = 'integer' THEN
        ALTER TABLE public.patient
            ADD COLUMN kurum_id_text varchar;

        UPDATE public.patient AS patient_row
        SET kurum_id_text = NULLIF(BTRIM(institution.kurum_code), '')
        FROM public.kurum AS institution
        WHERE institution.record_id = patient_row.kurum_id;

        ALTER TABLE public.patient
            DROP COLUMN kurum_id;

        ALTER TABLE public.patient
            RENAME COLUMN kurum_id_text TO kurum_id;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'public.kurum'::regclass
          AND conname = 'kurum_kurum_code_key'
    ) THEN
        ALTER TABLE public.kurum
            ADD CONSTRAINT kurum_kurum_code_key UNIQUE (kurum_code);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'public.patient'::regclass
          AND conname = 'patient_kurum_id_fkey'
    ) THEN
        ALTER TABLE public.patient
            ADD CONSTRAINT patient_kurum_id_fkey
            FOREIGN KEY (kurum_id) REFERENCES public.kurum(kurum_code);
    END IF;
END $$;

COMMIT;
