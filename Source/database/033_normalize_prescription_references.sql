-- Normalize prescription ownership, doctor/branch and ICD-10 references.
-- Run after 032_seed_icd10_diagnosis.sql.
BEGIN;

ALTER TABLE public.prescription
    ADD COLUMN IF NOT EXISTS doctor_branch_id integer,
    ADD COLUMN IF NOT EXISTS icd10_diagnosis_codes varchar[],
    ADD COLUMN IF NOT EXISTS legacy_patient_insurance_type varchar,
    ADD COLUMN IF NOT EXISTS legacy_invoice_type varchar;

CREATE TABLE IF NOT EXISTS public.patient_identity (
    tc_identity_no varchar PRIMARY KEY,
    patient_record_id integer NOT NULL REFERENCES public.patient(record_id)
);

INSERT INTO public.patient_identity (tc_identity_no, patient_record_id)
SELECT DISTINCT ON (BTRIM(tc_identity_no)) BTRIM(tc_identity_no), record_id
  FROM public.patient
 WHERE NULLIF(BTRIM(tc_identity_no), '') IS NOT NULL
 ORDER BY BTRIM(tc_identity_no), record_id DESC
ON CONFLICT (tc_identity_no) DO UPDATE
    SET patient_record_id=EXCLUDED.patient_record_id;

-- Preserve directory data before removing duplicated display fields.
INSERT INTO public.patient (tc_identity_no, first_name, last_name)
SELECT DISTINCT NULLIF(BTRIM(patient_tc_identity_no), ''),
       NULLIF(BTRIM(patient_first_name), ''), NULLIF(BTRIM(patient_last_name), '')
  FROM public.prescription p
 WHERE NULLIF(BTRIM(patient_tc_identity_no), '') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM public.patient patient
        WHERE patient.tc_identity_no = BTRIM(p.patient_tc_identity_no));

INSERT INTO public.patient (tc_identity_no)
SELECT DISTINCT NULLIF(BTRIM(recipient_tc_identity_no), '')
  FROM public.prescription p
 WHERE NULLIF(BTRIM(recipient_tc_identity_no), '') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM public.patient patient
        WHERE patient.tc_identity_no = BTRIM(p.recipient_tc_identity_no));

INSERT INTO public.patient_identity (tc_identity_no, patient_record_id)
SELECT DISTINCT ON (BTRIM(tc_identity_no)) BTRIM(tc_identity_no), record_id
  FROM public.patient
 WHERE NULLIF(BTRIM(tc_identity_no), '') IS NOT NULL
 ORDER BY BTRIM(tc_identity_no), record_id DESC
ON CONFLICT (tc_identity_no) DO UPDATE
    SET patient_record_id=EXCLUDED.patient_record_id;

INSERT INTO public.doctor (
    diploma_registration_no, diploma_no, first_name, last_name, certificate, branch_id)
SELECT DISTINCT ON (BTRIM(p.doctor_diploma_registration_no))
       BTRIM(p.doctor_diploma_registration_no), NULLIF(BTRIM(p.doctor_diploma_no), ''),
       NULLIF(BTRIM(p.doctor_first_name), ''), NULLIF(BTRIM(p.doctor_last_name), ''),
       NULLIF(BTRIM(p.doctor_certificate), ''), branch.branch_id
  FROM public.prescription p
  LEFT JOIN public.branches branch
    ON LOWER(REPLACE(branch.branch_name, ' ', '')) = LOWER(REPLACE(
       CASE WHEN POSITION('->' IN COALESCE(p.doctor_specialty, '')) > 0
            THEN SPLIT_PART(p.doctor_specialty, '->', 2)
            ELSE COALESCE(p.doctor_specialty, '') END, ' ', ''))
 WHERE NULLIF(BTRIM(p.doctor_diploma_registration_no), '') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM public.doctor doctor
        WHERE doctor.diploma_registration_no = BTRIM(p.doctor_diploma_registration_no))
 ORDER BY BTRIM(p.doctor_diploma_registration_no), p.version_number DESC, p.record_id DESC;

UPDATE public.prescription p
   SET doctor_branch_id = branch.branch_id
  FROM public.branches branch
 WHERE p.doctor_branch_id IS NULL
   AND LOWER(REPLACE(branch.branch_name, ' ', '')) = LOWER(REPLACE(
       CASE WHEN POSITION('->' IN COALESCE(p.doctor_specialty, '')) > 0
            THEN SPLIT_PART(p.doctor_specialty, '->', 2)
            ELSE COALESCE(p.doctor_specialty, '') END, ' ', ''));

-- Old records used a two-dimensional [code, description] matrix. The prescription
-- now keeps only codes; Turkish descriptions live in icd10_diagnosis.
INSERT INTO public.icd10_diagnosis (code, description)
SELECT DISTINCT BTRIM(p.icd10_diagnoses[row_no][1]),
       COALESCE(NULLIF(BTRIM(p.icd10_diagnoses[row_no][2]), ''),
                BTRIM(p.icd10_diagnoses[row_no][1]))
  FROM public.prescription p
 CROSS JOIN LATERAL generate_subscripts(p.icd10_diagnoses, 1) row_no
 WHERE NULLIF(BTRIM(p.icd10_diagnoses[row_no][1]), '') IS NOT NULL
ON CONFLICT (code) DO NOTHING;

UPDATE public.prescription p
   SET icd10_diagnosis_codes = ARRAY(
       SELECT DISTINCT BTRIM(p.icd10_diagnoses[row_no][1])
         FROM generate_subscripts(p.icd10_diagnoses, 1) row_no
        WHERE NULLIF(BTRIM(p.icd10_diagnoses[row_no][1]), '') IS NOT NULL
        ORDER BY BTRIM(p.icd10_diagnoses[row_no][1]));

-- Do not preserve an insurance value that was previously copied from coverage.
UPDATE public.prescription
   SET legacy_patient_insurance_type = patient_insurance_type,
       patient_insurance_type = NULL
 WHERE NULLIF(BTRIM(patient_insurance_type), '') IS NOT NULL
   AND LOWER(BTRIM(patient_insurance_type)) = LOWER(BTRIM(patient_coverage_type));

UPDATE public.prescription
   SET its_finalized = LOWER(BTRIM(COALESCE(medula_qr_status, ''))) = LOWER('Sonlandırıldı');

UPDATE public.prescription p
   SET legacy_invoice_type = p.invoice_type,
       invoice_type = CASE p.medula_type_value
       WHEN '1' THEN 'A Grubu' WHEN '2' THEN 'A Grubu' WHEN '3' THEN 'A Grubu'
       WHEN '4' THEN 'B Grubu' WHEN '5' THEN 'B Grubu' WHEN '6' THEN 'B Grubu'
       WHEN '28' THEN 'C Grubu Sıralı Dağıtım' WHEN '29' THEN 'C Grubu Sıralı Dağıtım' WHEN '30' THEN 'C Grubu Sıralı Dağıtım'
       WHEN '7' THEN 'C Grubu Kan Ürünü' WHEN '8' THEN 'C Grubu Kan Ürünü' WHEN '9' THEN 'C Grubu Kan Ürünü'
       WHEN '25' THEN 'G2D Grubu' WHEN '26' THEN 'G2D Grubu' WHEN '27' THEN 'G2D Grubu'
       WHEN '16' THEN 'Yeşil Kart Normal' WHEN '19' THEN 'Yeşil Kart Kan Ürünü'
       WHEN '99' THEN 'Şahıs Ödemeleri Reçeteleri'
       WHEN '40' THEN 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar'
       WHEN '41' THEN 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar'
       WHEN '42' THEN 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar'
       WHEN '43' THEN 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB'
       WHEN '44' THEN 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB'
       WHEN '45' THEN 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB'
       ELSE COALESCE(NULLIF(BTRIM(p.invoice_type), ''), p.medula_type_value)
       END
 WHERE NULLIF(BTRIM(p.medula_type_value), '') IS NOT NULL
   AND (NULLIF(BTRIM(p.invoice_type), '') IS NULL
        OR BTRIM(p.invoice_type) IN ('-', '–', '—', 'Seçiniz')
        OR p.invoice_type ILIKE 'Fatura %');

-- Retain superseded values under explicit legacy names. This keeps the migration
-- reversible while preventing the application from writing duplicated directory data.
ALTER TABLE public.prescription RENAME COLUMN icd10_diagnoses TO legacy_icd10_diagnoses;
ALTER TABLE public.prescription RENAME COLUMN icd10_diagnosis_codes TO icd10_diagnoses;
ALTER TABLE public.prescription RENAME COLUMN patient_first_name TO legacy_patient_first_name;
ALTER TABLE public.prescription RENAME COLUMN patient_last_name TO legacy_patient_last_name;
ALTER TABLE public.prescription RENAME COLUMN doctor_diploma_no TO legacy_doctor_diploma_no;
ALTER TABLE public.prescription RENAME COLUMN doctor_first_name TO legacy_doctor_first_name;
ALTER TABLE public.prescription RENAME COLUMN doctor_last_name TO legacy_doctor_last_name;
ALTER TABLE public.prescription RENAME COLUMN doctor_specialty TO legacy_doctor_specialty;
ALTER TABLE public.prescription RENAME COLUMN doctor_certificate TO legacy_doctor_certificate;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='prescription_patient_tc_identity_no_fkey') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT prescription_patient_tc_identity_no_fkey
            FOREIGN KEY (patient_tc_identity_no) REFERENCES public.patient_identity(tc_identity_no);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='prescription_recipient_tc_identity_no_fkey') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT prescription_recipient_tc_identity_no_fkey
            FOREIGN KEY (recipient_tc_identity_no) REFERENCES public.patient_identity(tc_identity_no);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='prescription_doctor_registration_no_fkey') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT prescription_doctor_registration_no_fkey
            FOREIGN KEY (doctor_diploma_registration_no)
            REFERENCES public.doctor(diploma_registration_no);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='prescription_doctor_branch_id_fkey') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT prescription_doctor_branch_id_fkey
            FOREIGN KEY (doctor_branch_id) REFERENCES public.branches(branch_id);
    END IF;
END $$;

CREATE OR REPLACE FUNCTION public.validate_prescription_icd10_codes()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE unknown_code varchar;
BEGIN
    SELECT code INTO unknown_code
      FROM unnest(COALESCE(NEW.icd10_diagnoses, ARRAY[]::varchar[])) code
     WHERE NOT EXISTS (SELECT 1 FROM public.icd10_diagnosis ref WHERE ref.code=code)
     LIMIT 1;
    IF unknown_code IS NOT NULL THEN
        RAISE EXCEPTION 'Unknown ICD-10 code: %', unknown_code USING ERRCODE='23503';
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_prescription_icd10_codes ON public.prescription;
CREATE TRIGGER trg_prescription_icd10_codes
BEFORE INSERT OR UPDATE OF icd10_diagnoses ON public.prescription
FOR EACH ROW EXECUTE FUNCTION public.validate_prescription_icd10_codes();

CREATE INDEX IF NOT EXISTS ix_prescription_doctor_branch ON public.prescription(doctor_branch_id);
CREATE INDEX IF NOT EXISTS ix_prescription_icd10_codes ON public.prescription USING gin(icd10_diagnoses);

COMMIT;
