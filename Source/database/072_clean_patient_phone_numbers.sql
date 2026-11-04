BEGIN;

CREATE TABLE IF NOT EXISTS public.pharmacy_schema_migration (
    migration_id varchar PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

WITH parsed AS (
    SELECT patient.record_id,
           phone.position,
           match.parts[2] AS purpose,
           regexp_replace(match.parts[1],'\D','','g') AS digits
      FROM public.patient patient
      CROSS JOIN LATERAL unnest(COALESCE(patient.phone_numbers,ARRAY[]::varchar[]))
          WITH ORDINALITY phone(value,position)
      CROSS JOIN LATERAL (
          SELECT regexp_match(BTRIM(phone.value),'^(.*[0-9])(?:\s+([^0-9].*))?$') AS parts
      ) match
     WHERE match.parts IS NOT NULL
), normalized AS (
    SELECT record_id, position, purpose,
           CASE
               WHEN digits ~ '^0[0-9]{10}$' THEN digits
               WHEN digits ~ '^[1-9][0-9]{9}$' THEN '0' || digits
               WHEN digits ~ '^90[1-9][0-9]{9}$' THEN '0' || SUBSTRING(digits FROM 3)
               ELSE NULL
           END AS digits
      FROM parsed
), cleaned AS (
    SELECT patient.record_id,
           (CASE WHEN COUNT(normalized.digits) FILTER (WHERE normalized.digits IS NOT NULL)=0 THEN NULL
                ELSE ARRAY_AGG(
                    '0(' || SUBSTRING(normalized.digits FROM 2 FOR 3) || ') '
                    || SUBSTRING(normalized.digits FROM 5 FOR 3) || ' '
                    || SUBSTRING(normalized.digits FROM 8 FOR 2) || ' '
                    || SUBSTRING(normalized.digits FROM 10 FOR 2)
                    || CASE WHEN NULLIF(BTRIM(normalized.purpose),'') IS NULL THEN ''
                            ELSE ' ' || BTRIM(normalized.purpose) END
                    ORDER BY normalized.position)
                    FILTER (WHERE normalized.digits IS NOT NULL)
           END)::varchar[] AS phone_numbers
      FROM public.patient patient
      LEFT JOIN normalized ON normalized.record_id=patient.record_id
     GROUP BY patient.record_id
)
UPDATE public.patient patient
   SET phone_numbers=cleaned.phone_numbers
  FROM cleaned
 WHERE patient.record_id=cleaned.record_id
   AND patient.phone_numbers IS DISTINCT FROM cleaned.phone_numbers;

INSERT INTO public.pharmacy_schema_migration(migration_id)
VALUES ('072_clean_patient_phone_numbers')
ON CONFLICT (migration_id) DO NOTHING;

COMMIT;
