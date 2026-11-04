BEGIN;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'doctor'
          AND column_name = 'specialty'
          AND data_type = 'character varying'
    ) THEN
        ALTER TABLE public.doctor
            ALTER COLUMN specialty TYPE varchar[]
            USING CASE
                WHEN NULLIF(BTRIM(specialty), '') IS NULL THEN NULL
                ELSE ARRAY[BTRIM(specialty)]::varchar[]
            END;
    END IF;
END $$;

COMMIT;
