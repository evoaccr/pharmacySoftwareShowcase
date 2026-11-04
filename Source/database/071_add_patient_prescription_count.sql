BEGIN;

CREATE TABLE IF NOT EXISTS public.pharmacy_schema_migration (
    migration_id varchar PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.patient
    ADD COLUMN IF NOT EXISTS prescription_count integer NOT NULL DEFAULT 0;

CREATE OR REPLACE FUNCTION public.adjust_patient_prescription_count(
    identity_no varchar,
    adjustment integer)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF identity_no IS NULL OR adjustment = 0 THEN
        RETURN;
    END IF;

    UPDATE public.patient patient
       SET prescription_count=GREATEST(patient.prescription_count + adjustment, 0)
      FROM public.patient_identity identity_row
     WHERE identity_row.tc_identity_no=identity_no
       AND patient.record_id=identity_row.patient_record_id;
END $$;

CREATE OR REPLACE FUNCTION public.track_patient_prescription_count()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    old_is_counted boolean := TG_OP <> 'INSERT'
        AND OLD.patient_tc_identity_no IS NOT NULL
        AND NOT COALESCE(OLD.is_deleted, false);
    new_is_counted boolean := TG_OP <> 'DELETE'
        AND NEW.patient_tc_identity_no IS NOT NULL
        AND NOT COALESCE(NEW.is_deleted, false);
BEGIN
    IF old_is_counted AND (NOT new_is_counted OR OLD.patient_tc_identity_no IS DISTINCT FROM NEW.patient_tc_identity_no) THEN
        PERFORM public.adjust_patient_prescription_count(OLD.patient_tc_identity_no, -1);
    END IF;
    IF new_is_counted AND (NOT old_is_counted OR NEW.patient_tc_identity_no IS DISTINCT FROM OLD.patient_tc_identity_no) THEN
        PERFORM public.adjust_patient_prescription_count(NEW.patient_tc_identity_no, 1);
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;

DROP TRIGGER IF EXISTS trg_prescription_patient_count ON public.prescription;
CREATE TRIGGER trg_prescription_patient_count
AFTER INSERT OR DELETE OR UPDATE OF patient_tc_identity_no, is_deleted ON public.prescription
FOR EACH ROW EXECUTE FUNCTION public.track_patient_prescription_count();

CREATE OR REPLACE FUNCTION public.refresh_patient_prescription_count_for_identity()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    patient_id integer;
    refreshed_count integer;
BEGIN
    FOREACH patient_id IN ARRAY ARRAY[
        CASE WHEN TG_OP='INSERT' THEN NULL ELSE OLD.patient_record_id END,
        CASE WHEN TG_OP='DELETE' THEN NULL ELSE NEW.patient_record_id END
    ]
    LOOP
        IF patient_id IS NOT NULL THEN
            SELECT COUNT(*)::integer
              INTO refreshed_count
              FROM public.patient_identity identity_row
              JOIN public.prescription prescription
                ON prescription.patient_tc_identity_no=identity_row.tc_identity_no
             WHERE identity_row.patient_record_id=patient_id
               AND NOT COALESCE(prescription.is_deleted, false);
            UPDATE public.patient patient
               SET prescription_count=refreshed_count
             WHERE patient.record_id=patient_id
               AND patient.prescription_count IS DISTINCT FROM refreshed_count;
        END IF;

        EXIT WHEN TG_OP='UPDATE' AND OLD.patient_record_id=NEW.patient_record_id;
    END LOOP;
    RETURN COALESCE(NEW, OLD);
END $$;

DROP TRIGGER IF EXISTS trg_patient_identity_prescription_count ON public.patient_identity;
CREATE TRIGGER trg_patient_identity_prescription_count
AFTER INSERT OR DELETE OR UPDATE OF tc_identity_no, patient_record_id ON public.patient_identity
FOR EACH ROW EXECUTE FUNCTION public.refresh_patient_prescription_count_for_identity();

WITH counts AS (
    SELECT identity_row.patient_record_id, COUNT(*)::integer AS prescription_count
      FROM public.patient_identity identity_row
      JOIN public.prescription prescription
        ON prescription.patient_tc_identity_no=identity_row.tc_identity_no
       AND NOT COALESCE(prescription.is_deleted, false)
     GROUP BY identity_row.patient_record_id
)
UPDATE public.patient patient
   SET prescription_count=COALESCE(counts.prescription_count, 0)
  FROM (SELECT patient_row.record_id,
               COALESCE(counts.prescription_count, 0) AS prescription_count
          FROM public.patient patient_row
          LEFT JOIN counts ON counts.patient_record_id=patient_row.record_id) counts
 WHERE patient.record_id=counts.record_id
   AND patient.prescription_count IS DISTINCT FROM counts.prescription_count;

CREATE INDEX IF NOT EXISTS ix_patient_prescription_count
    ON public.patient(prescription_count DESC);

INSERT INTO public.pharmacy_schema_migration(migration_id)
VALUES ('071_add_patient_prescription_count')
ON CONFLICT (migration_id) DO NOTHING;

COMMIT;
