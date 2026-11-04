CREATE TABLE IF NOT EXISTS public.pharmacy_schema_migration (
    migration_id varchar PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.prescription_invoice_type (
    invoice_type_code smallint PRIMARY KEY,
    invoice_group_code smallint NOT NULL,
    invoice_group_name varchar NOT NULL,
    return_status_code smallint NOT NULL,
    return_status_name varchar NOT NULL,
    display_name varchar NOT NULL,
    CONSTRAINT ck_prescription_invoice_type_return_status
        CHECK (return_status_code BETWEEN 0 AND 2),
    CONSTRAINT ux_prescription_invoice_type_variant
        UNIQUE (invoice_group_code, return_status_code)
);

INSERT INTO public.prescription_invoice_type (
    invoice_type_code, invoice_group_code, invoice_group_name,
    return_status_code, return_status_name, display_name)
VALUES
    (1, 1, 'A Grubu', 0, 'Yok', 'A Grubu'),
    (2, 1, 'A Grubu', 1, 'İçerikten İade', 'A Grubu İçerikten İade'),
    (3, 1, 'A Grubu', 2, 'Mevzuattan İade', 'A Grubu Mevzuattan İade'),
    (4, 2, 'B Grubu', 0, 'Yok', 'B Grubu'),
    (5, 2, 'B Grubu', 1, 'İçerikten İade', 'B Grubu İçerikten İade'),
    (6, 2, 'B Grubu', 2, 'Mevzuattan İade', 'B Grubu Mevzuattan İade'),
    (28, 3, 'C Grubu Sıralı Dağıtım', 0, 'Yok', 'C Grubu Sıralı Dağıtım'),
    (29, 3, 'C Grubu Sıralı Dağıtım', 1, 'İçerikten İade', 'C Grubu Sıralı Dağıtım İçerikten İade'),
    (30, 3, 'C Grubu Sıralı Dağıtım', 2, 'Mevzuattan İade', 'C Grubu Sıralı Dağıtım Mevzuattan İade'),
    (7, 4, 'C Grubu Kan Ürünü', 0, 'Yok', 'C Grubu Kan Ürünü'),
    (8, 4, 'C Grubu Kan Ürünü', 1, 'İçerikten İade', 'C Grubu Kan Ürünü İçerikten İade'),
    (9, 4, 'C Grubu Kan Ürünü', 2, 'Mevzuattan İade', 'C Grubu Kan Ürünü Mevzuattan İade'),
    (25, 5, 'G2D Grubu', 0, 'Yok', 'G2D Grubu'),
    (26, 5, 'G2D Grubu', 1, 'İçerikten İade', 'G2D Grubu İçerikten İade'),
    (27, 5, 'G2D Grubu', 2, 'Mevzuattan İade', 'G2D Grubu Mevzuattan İade'),
    (16, 6, 'Yeşil Kart Normal', 0, 'Yok', 'Yeşil Kart Normal'),
    (19, 7, 'Yeşil Kart Kan Ürünü', 0, 'Yok', 'Yeşil Kart Kan Ürünü'),
    (10, 8, 'Yurtdışı İlaç Normal', 0, 'Yok', 'Yurtdışı İlaç Normal'),
    (11, 8, 'Yurtdışı İlaç Normal', 1, 'İçerikten İade', 'Yurtdışı İlaç Normal İçerikten İade'),
    (12, 8, 'Yurtdışı İlaç Normal', 2, 'Mevzuattan İade', 'Yurtdışı İlaç Normal Mevzuattan İade'),
    (13, 9, 'Yurtdışı İlaç Kan Ürünü', 0, 'Yok', 'Yurtdışı İlaç Kan Ürünü'),
    (14, 9, 'Yurtdışı İlaç Kan Ürünü', 1, 'İçerikten İade', 'Yurtdışı İlaç Kan Ürünü İçerikten İade'),
    (15, 9, 'Yurtdışı İlaç Kan Ürünü', 2, 'Mevzuattan İade', 'Yurtdışı İlaç Kan Ürünü Mevzuattan İade'),
    (99, 10, 'Şahıs Ödemeleri Reçeteleri', 0, 'Yok', 'Şahıs Ödemeleri Reçeteleri'),
    (22, 11, 'Yurtdışı Sigortalı Normal', 0, 'Yok', 'Yurtdışı Sigortalı Normal'),
    (23, 11, 'Yurtdışı Sigortalı Normal', 1, 'İçerikten İade', 'Yurtdışı Sigortalı Normal İçerikten İade'),
    (24, 11, 'Yurtdışı Sigortalı Normal', 2, 'Mevzuattan İade', 'Yurtdışı Sigortalı Normal Mevzuattan İade'),
    (31, 12, 'Yurtdışı Sigortalı Kan Ürünü', 0, 'Yok', 'Yurtdışı Sigortalı Kan Ürünü'),
    (32, 12, 'Yurtdışı Sigortalı Kan Ürünü', 1, 'İçerikten İade', 'Yurtdışı Sigortalı Kan Ürünü İçerikten İade'),
    (33, 12, 'Yurtdışı Sigortalı Kan Ürünü', 2, 'Mevzuattan İade', 'Yurtdışı Sigortalı Kan Ürünü Mevzuattan İade'),
    (34, 13, 'Yurtdışı İlaç Yurtdışı Sigortalı Normal', 0, 'Yok', 'Yurtdışı İlaç Yurtdışı Sigortalı Normal'),
    (35, 13, 'Yurtdışı İlaç Yurtdışı Sigortalı Normal', 1, 'İçerikten İade', 'Yurtdışı İlaç Yurtdışı Sigortalı Normal İçerikten İade'),
    (36, 13, 'Yurtdışı İlaç Yurtdışı Sigortalı Normal', 2, 'Mevzuattan İade', 'Yurtdışı İlaç Yurtdışı Sigortalı Normal Mevzuattan İade'),
    (37, 14, 'Yurtdışı İlaç Yurtdışı Sigortalı Kan Ürünü', 0, 'Yok', 'Yurtdışı İlaç Yurtdışı Sigortalı Kan Ürünü'),
    (38, 14, 'Yurtdışı İlaç Yurtdışı Sigortalı Kan Ürünü', 1, 'İçerikten İade', 'Yurtdışı İlaç Yurtdışı Sigortalı Kan Ürünü İçerikten İade'),
    (39, 14, 'Yurtdışı İlaç Yurtdışı Sigortalı Kan Ürünü', 2, 'Mevzuattan İade', 'Yurtdışı İlaç Yurtdışı Sigortalı Kan Ürünü Mevzuattan İade'),
    (40, 15, 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar', 0, 'Yok', 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar'),
    (41, 15, 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar', 1, 'İçerikten İade', 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar İçerikten İade'),
    (42, 15, 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar', 2, 'Mevzuattan İade', 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar Mevzuattan İade'),
    (43, 16, 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB', 0, 'Yok', 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB'),
    (44, 16, 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB', 1, 'İçerikten İade', 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB İçerikten İade'),
    (45, 16, 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB', 2, 'Mevzuattan İade', 'Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB Mevzuattan İade')
ON CONFLICT (invoice_type_code) DO UPDATE SET
    invoice_group_code = EXCLUDED.invoice_group_code,
    invoice_group_name = EXCLUDED.invoice_group_name,
    return_status_code = EXCLUDED.return_status_code,
    return_status_name = EXCLUDED.return_status_name,
    display_name = EXCLUDED.display_name;

CREATE TABLE IF NOT EXISTS public.prescription (
    record_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    e_prescription_no varchar,
    medula_prescription_no varchar,
    invoice_type_code smallint REFERENCES public.prescription_invoice_type(invoice_type_code),
    qr_status_code smallint,
    prescription_type_code smallint,
    prescription_subtype_code smallint,
    provision_type_code smallint,
    protocol_no varchar,
    facility_code varchar,
    prescription_date date,
    medication_purchase_date date,
    has_qr_codes boolean,
    is_sequential_distribution boolean,
    patient_tc_identity_no varchar,
    patient_type varchar,
    patient_insurance_type varchar,
    patient_coverage_type varchar,
    recipient_tc_identity_no varchar,
    recipient_relationship varchar,
    doctor_diploma_registration_no varchar,
    doctor_branch_id integer,
    doctor_certificate varchar,
    registered_content varchar[][],
    final_content varchar[][],
    icd10_diagnoses varchar[],
    prescription_note text,
    prescription_copayment_cash numeric(14,2),
    prescription_copayment_salary numeric(14,2),
    examination_copayment_cash numeric(14,2),
    examination_copayment_salary numeric(14,2),
    medication_copayment numeric(14,2),
    equivalent_price_difference numeric(14,2),
    institution_total_payment numeric(14,2),
    patient_total_payment numeric(14,2),
    prescription_total numeric(14,2),
    qr_codes varchar[],
    saved_by_user_id varchar,
    saved_at timestamptz NOT NULL DEFAULT now(),
    prescription_uid uuid,
    version_number integer,
    medula_filter_date date,
    imported_from_medula boolean NOT NULL DEFAULT false,
    imported_from_farmassist boolean NOT NULL DEFAULT false,
    identity_fingerprint varchar,
    list_status_code varchar,
    list_barcode_status varchar,
    list_medical_report varchar,
    list_document_status varchar,
    medula_page_number integer,
    medula_row_index integer,
    state_fingerprint varchar,
    is_deleted boolean NOT NULL DEFAULT false
);

ALTER TABLE public.prescription
    ADD COLUMN IF NOT EXISTS invoice_type_code smallint,
    ADD COLUMN IF NOT EXISTS qr_status_code smallint,
    ADD COLUMN IF NOT EXISTS prescription_type_code smallint,
    ADD COLUMN IF NOT EXISTS prescription_subtype_code smallint,
    ADD COLUMN IF NOT EXISTS provision_type_code smallint,
    ADD COLUMN IF NOT EXISTS e_prescription_no varchar,
    ADD COLUMN IF NOT EXISTS medula_prescription_no varchar,
    ADD COLUMN IF NOT EXISTS protocol_no varchar,
    ADD COLUMN IF NOT EXISTS facility_code varchar,
    ADD COLUMN IF NOT EXISTS prescription_date date,
    ADD COLUMN IF NOT EXISTS medication_purchase_date date,
    ADD COLUMN IF NOT EXISTS has_qr_codes boolean,
    ADD COLUMN IF NOT EXISTS is_sequential_distribution boolean,
    ADD COLUMN IF NOT EXISTS patient_tc_identity_no varchar,
    ADD COLUMN IF NOT EXISTS patient_type varchar,
    ADD COLUMN IF NOT EXISTS patient_insurance_type varchar,
    ADD COLUMN IF NOT EXISTS patient_coverage_type varchar,
    ADD COLUMN IF NOT EXISTS recipient_tc_identity_no varchar,
    ADD COLUMN IF NOT EXISTS recipient_relationship varchar,
    ADD COLUMN IF NOT EXISTS doctor_diploma_registration_no varchar,
    ADD COLUMN IF NOT EXISTS doctor_branch_id integer,
    ADD COLUMN IF NOT EXISTS doctor_certificate varchar,
    ADD COLUMN IF NOT EXISTS registered_content varchar[][],
    ADD COLUMN IF NOT EXISTS final_content varchar[][],
    ADD COLUMN IF NOT EXISTS icd10_diagnoses varchar[],
    ADD COLUMN IF NOT EXISTS prescription_note text,
    ADD COLUMN IF NOT EXISTS prescription_copayment_cash numeric(14,2),
    ADD COLUMN IF NOT EXISTS prescription_copayment_salary numeric(14,2),
    ADD COLUMN IF NOT EXISTS examination_copayment_cash numeric(14,2),
    ADD COLUMN IF NOT EXISTS examination_copayment_salary numeric(14,2),
    ADD COLUMN IF NOT EXISTS medication_copayment numeric(14,2),
    ADD COLUMN IF NOT EXISTS equivalent_price_difference numeric(14,2),
    ADD COLUMN IF NOT EXISTS institution_total_payment numeric(14,2),
    ADD COLUMN IF NOT EXISTS patient_total_payment numeric(14,2),
    ADD COLUMN IF NOT EXISTS prescription_total numeric(14,2),
    ADD COLUMN IF NOT EXISTS qr_codes varchar[],
    ADD COLUMN IF NOT EXISTS saved_by_user_id varchar,
    ADD COLUMN IF NOT EXISTS saved_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS prescription_uid uuid,
    ADD COLUMN IF NOT EXISTS version_number integer,
    ADD COLUMN IF NOT EXISTS medula_filter_date date,
    ADD COLUMN IF NOT EXISTS imported_from_medula boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS imported_from_farmassist boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS identity_fingerprint varchar,
    ADD COLUMN IF NOT EXISTS list_status_code varchar,
    ADD COLUMN IF NOT EXISTS list_barcode_status varchar,
    ADD COLUMN IF NOT EXISTS list_medical_report varchar,
    ADD COLUMN IF NOT EXISTS list_document_status varchar,
    ADD COLUMN IF NOT EXISTS medula_page_number integer,
    ADD COLUMN IF NOT EXISTS medula_row_index integer,
    ADD COLUMN IF NOT EXISTS state_fingerprint varchar,
    ADD COLUMN IF NOT EXISTS is_deleted boolean NOT NULL DEFAULT false;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM public.pharmacy_schema_migration
         WHERE migration_id='066_prescription_owned_doctor_certificate') THEN
        UPDATE public.prescription prescription
           SET doctor_certificate = doctor.certificate
          FROM public.doctor doctor
         WHERE doctor.diploma_registration_no = prescription.doctor_diploma_registration_no
           AND NULLIF(BTRIM(prescription.doctor_certificate), '') IS NULL
           AND NULLIF(BTRIM(doctor.certificate), '') IS NOT NULL;

        INSERT INTO public.pharmacy_schema_migration (migration_id)
        VALUES ('066_prescription_owned_doctor_certificate')
        ON CONFLICT (migration_id) DO NOTHING;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS public.prescription_log (
    log_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    prescription_uid uuid NOT NULL,
    version_number integer NOT NULL,
    action_type varchar NOT NULL,
    update_datetime timestamptz NOT NULL DEFAULT now(),
    user_id varchar,
    prescription_snapshot jsonb NOT NULL
);

DO $$
DECLARE has_legacy_columns boolean;
BEGIN
    SELECT EXISTS (
        SELECT 1 FROM information_schema.columns
         WHERE table_schema='public' AND table_name='prescription'
           AND column_name IN ('medula_type_value', 'invoice_type', 'medula_qr_status',
                               'prescription_type', 'prescription_subtype', 'provision_type'))
      INTO has_legacy_columns;

    IF has_legacy_columns AND NOT EXISTS (
        SELECT 1 FROM public.pharmacy_schema_migration
         WHERE migration_id='065_normalize_prescription_codes') THEN
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='prescription' AND column_name='medula_type_value') THEN
            EXECUTE $sql$
                UPDATE public.prescription p
                   SET invoice_type_code = CASE
                       WHEN NULLIF(BTRIM(p.medula_type_value), '') ~ '^[0-9]+$'
                            AND EXISTS (SELECT 1 FROM public.prescription_invoice_type ref
                                         WHERE ref.invoice_type_code=BTRIM(p.medula_type_value)::smallint)
                           THEN BTRIM(p.medula_type_value)::smallint
                       WHEN BTRIM(COALESCE(p.invoice_type, ''))='A Grubu' THEN 1
                       WHEN BTRIM(COALESCE(p.invoice_type, ''))='B Grubu' THEN 4
                       WHEN BTRIM(COALESCE(p.invoice_type, ''))='C Grubu Sıralı Dağıtım' THEN 28
                       WHEN BTRIM(COALESCE(p.invoice_type, ''))='C Grubu Kan Ürünü' THEN 7
                       WHEN BTRIM(COALESCE(p.invoice_type, ''))='G2D Grubu' THEN 25
                       WHEN BTRIM(COALESCE(p.invoice_type, ''))='Yeşil Kart Normal' THEN 16
                       WHEN BTRIM(COALESCE(p.invoice_type, ''))='Yeşil Kart Kan Ürünü' THEN 19
                       ELSE p.invoice_type_code END
            $sql$;
        END IF;

        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='prescription' AND column_name='medula_qr_status') THEN
            EXECUTE $sql$
                UPDATE public.prescription p
                   SET qr_status_code = CASE
                       WHEN p.is_deleted OR LOWER(COALESCE(p.medula_qr_status, '')) LIKE '%silin%' THEN 3
                       WHEN LOWER(COALESCE(p.medula_qr_status, '')) LIKE '%sonlandırılmadı%'
                            OR LOWER(COALESCE(p.medula_qr_status, '')) LIKE '%sonlandirilmadi%'
                            OR LOWER(COALESCE(p.medula_qr_status, '')) LIKE '%karekodlu ilaç var%'
                            OR LOWER(COALESCE(p.medula_qr_status, '')) LIKE '%karekodlu ilac var%' THEN 1
                       WHEN COALESCE(p.its_finalized, false)
                            OR LOWER(COALESCE(p.medula_qr_status, '')) LIKE '%sonlandırıldı%'
                            OR LOWER(COALESCE(p.medula_qr_status, '')) LIKE '%sonlandirildi%' THEN 2
                       WHEN LOWER(COALESCE(p.medula_qr_status, '')) LIKE '%yok%' THEN 0
                       ELSE p.qr_status_code END
            $sql$;
        END IF;

        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='prescription' AND column_name='prescription_type') THEN
            EXECUTE $sql$
                UPDATE public.prescription SET prescription_type_code = CASE
                    WHEN BTRIM(COALESCE(prescription_type, '')) ~ '^[1-5]$' THEN BTRIM(prescription_type)::smallint
                    WHEN LOWER(BTRIM(COALESCE(prescription_type, ''))) IN ('normal', 'standart') THEN 1
                    WHEN LOWER(BTRIM(COALESCE(prescription_type, ''))) LIKE 'k%rm%z%' THEN 2
                    WHEN LOWER(BTRIM(COALESCE(prescription_type, '')))='turuncu' THEN 3
                    WHEN LOWER(BTRIM(COALESCE(prescription_type, '')))='mor' THEN 4
                    WHEN LOWER(BTRIM(COALESCE(prescription_type, ''))) LIKE 'ye%il' THEN 5
                    ELSE prescription_type_code END
            $sql$;
        END IF;

        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='prescription' AND column_name='prescription_subtype') THEN
            EXECUTE $sql$
                UPDATE public.prescription SET prescription_subtype_code = CASE
                    WHEN BTRIM(COALESCE(prescription_subtype, '')) ~ '^[1-8]$' THEN BTRIM(prescription_subtype)::smallint
                    WHEN LOWER(BTRIM(COALESCE(prescription_subtype, '')))='ayaktan' THEN 1
                    WHEN LOWER(BTRIM(COALESCE(prescription_subtype, '')))='yatan' THEN 2
                    WHEN LOWER(BTRIM(COALESCE(prescription_subtype, '')))='taburcu' THEN 3
                    WHEN LOWER(BTRIM(COALESCE(prescription_subtype, ''))) LIKE 'g%n%birlik' THEN 4
                    WHEN LOWER(BTRIM(COALESCE(prescription_subtype, '')))='acil' THEN 5
                    WHEN LOWER(BTRIM(COALESCE(prescription_subtype, ''))) LIKE 'ye%il alan' THEN 6
                    WHEN LOWER(BTRIM(COALESCE(prescription_subtype, ''))) LIKE 'evde bak%m' THEN 7
                    WHEN LOWER(BTRIM(COALESCE(prescription_subtype, ''))) LIKE 'gezici%' THEN 8
                    ELSE prescription_subtype_code END
            $sql$;
        END IF;

        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='prescription' AND column_name='provision_type') THEN
            EXECUTE $sql$
                UPDATE public.prescription SET provision_type_code = CASE
                    WHEN BTRIM(COALESCE(provision_type, '')) ~ '^[1-8]$' THEN BTRIM(provision_type)::smallint
                    WHEN LOWER(BTRIM(COALESCE(provision_type, '')))='normal' THEN 1
                    WHEN LOWER(BTRIM(COALESCE(provision_type, ''))) LIKE 'trafik%' THEN 2
                    WHEN LOWER(BTRIM(COALESCE(provision_type, ''))) LIKE 'do%al afet' THEN 3
                    WHEN LOWER(BTRIM(COALESCE(provision_type, ''))) LIKE 'adli%' THEN 4
                    WHEN LOWER(BTRIM(COALESCE(provision_type, ''))) LIKE 'i% kazas%' THEN 5
                    WHEN LOWER(BTRIM(COALESCE(provision_type, ''))) LIKE 'meslek%' THEN 6
                    WHEN LOWER(BTRIM(COALESCE(provision_type, ''))) LIKE 'anal%k%' THEN 7
                    WHEN LOWER(BTRIM(COALESCE(provision_type, ''))) LIKE '%3713%21%' THEN 8
                    ELSE provision_type_code END
            $sql$;
        END IF;

        UPDATE public.prescription_log log
           SET prescription_snapshot =
               (log.prescription_snapshot
                    - 'medula_type_value' - 'invoice_type' - 'legacy_invoice_type'
                    - 'medula_qr_status' - 'its_finalized'
                    - 'prescription_type' - 'prescription_subtype' - 'provision_type'
                    - 'legacy_patient_first_name' - 'legacy_patient_last_name'
                    - 'legacy_patient_insurance_type' - 'legacy_doctor_diploma_no'
                    - 'legacy_doctor_first_name' - 'legacy_doctor_last_name'
                    - 'legacy_doctor_specialty' - 'legacy_doctor_certificate'
                    - 'legacy_icd10_diagnoses')
               || jsonb_strip_nulls(jsonb_build_object(
                    'invoice_type_code', CASE
                        WHEN COALESCE(log.prescription_snapshot->>'medula_type_value', '') ~ '^[0-9]+$'
                             AND EXISTS (SELECT 1 FROM public.prescription_invoice_type ref
                                          WHERE ref.invoice_type_code=(log.prescription_snapshot->>'medula_type_value')::smallint)
                            THEN (log.prescription_snapshot->>'medula_type_value')::smallint
                        WHEN log.prescription_snapshot->>'invoice_type'='A Grubu' THEN 1
                        WHEN log.prescription_snapshot->>'invoice_type'='B Grubu' THEN 4
                        WHEN log.prescription_snapshot->>'invoice_type'='C Grubu Sıralı Dağıtım' THEN 28
                        WHEN log.prescription_snapshot->>'invoice_type'='C Grubu Kan Ürünü' THEN 7
                        WHEN log.prescription_snapshot->>'invoice_type'='G2D Grubu' THEN 25
                        ELSE NULL END,
                    'qr_status_code', CASE
                        WHEN COALESCE((log.prescription_snapshot->>'is_deleted')::boolean, false)
                             OR LOWER(COALESCE(log.prescription_snapshot->>'medula_qr_status', '')) LIKE '%silin%' THEN 3
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'medula_qr_status', '')) LIKE '%sonlandırılmadı%'
                             OR LOWER(COALESCE(log.prescription_snapshot->>'medula_qr_status', '')) LIKE '%karekodlu ilaç var%' THEN 1
                        WHEN COALESCE((log.prescription_snapshot->>'its_finalized')::boolean, false)
                             OR LOWER(COALESCE(log.prescription_snapshot->>'medula_qr_status', '')) LIKE '%sonlandırıldı%' THEN 2
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'medula_qr_status', '')) LIKE '%yok%' THEN 0
                        ELSE NULL END,
                    'prescription_type_code', CASE
                        WHEN COALESCE(log.prescription_snapshot->>'prescription_type', '') ~ '^[1-5]$'
                            THEN (log.prescription_snapshot->>'prescription_type')::smallint
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_type', '')) IN ('normal', 'standart') THEN 1
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_type', '')) LIKE 'k%rm%z%' THEN 2
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_type', ''))='turuncu' THEN 3
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_type', ''))='mor' THEN 4
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_type', '')) LIKE 'ye%il' THEN 5
                        ELSE NULL END,
                    'prescription_subtype_code', CASE
                        WHEN COALESCE(log.prescription_snapshot->>'prescription_subtype', '') ~ '^[1-8]$'
                            THEN (log.prescription_snapshot->>'prescription_subtype')::smallint
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_subtype', ''))='ayaktan' THEN 1
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_subtype', ''))='yatan' THEN 2
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_subtype', ''))='taburcu' THEN 3
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_subtype', '')) LIKE 'g%n%birlik' THEN 4
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_subtype', ''))='acil' THEN 5
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_subtype', '')) LIKE 'ye%il alan' THEN 6
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_subtype', '')) LIKE 'evde bak%m' THEN 7
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'prescription_subtype', '')) LIKE 'gezici%' THEN 8
                        ELSE NULL END,
                    'provision_type_code', CASE
                        WHEN COALESCE(log.prescription_snapshot->>'provision_type', '') ~ '^[1-8]$'
                            THEN (log.prescription_snapshot->>'provision_type')::smallint
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'provision_type', ''))='normal' THEN 1
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'provision_type', '')) LIKE 'trafik%' THEN 2
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'provision_type', '')) LIKE 'do%al afet' THEN 3
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'provision_type', '')) LIKE 'adli%' THEN 4
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'provision_type', '')) LIKE 'i% kazas%' THEN 5
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'provision_type', '')) LIKE 'meslek%' THEN 6
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'provision_type', '')) LIKE 'anal%k%' THEN 7
                        WHEN LOWER(COALESCE(log.prescription_snapshot->>'provision_type', '')) LIKE '%3713%21%' THEN 8
                        ELSE NULL END))
         WHERE log.prescription_snapshot ?| ARRAY[
             'medula_type_value', 'invoice_type', 'legacy_invoice_type', 'medula_qr_status',
             'its_finalized', 'prescription_type', 'prescription_subtype', 'provision_type',
             'legacy_patient_first_name', 'legacy_patient_last_name', 'legacy_patient_insurance_type',
             'legacy_doctor_diploma_no', 'legacy_doctor_first_name', 'legacy_doctor_last_name',
             'legacy_doctor_specialty', 'legacy_doctor_certificate', 'legacy_icd10_diagnoses'];

        INSERT INTO public.pharmacy_schema_migration (migration_id)
        VALUES ('065_normalize_prescription_codes')
        ON CONFLICT (migration_id) DO NOTHING;
    ELSIF NOT EXISTS (
        SELECT 1 FROM public.pharmacy_schema_migration
         WHERE migration_id='065_normalize_prescription_codes') THEN
        INSERT INTO public.pharmacy_schema_migration (migration_id)
        VALUES ('065_normalize_prescription_codes');
    END IF;
END $$;

ALTER TABLE public.prescription
    DROP COLUMN IF EXISTS legacy_patient_first_name,
    DROP COLUMN IF EXISTS legacy_patient_last_name,
    DROP COLUMN IF EXISTS legacy_patient_insurance_type,
    DROP COLUMN IF EXISTS legacy_doctor_diploma_no,
    DROP COLUMN IF EXISTS legacy_doctor_first_name,
    DROP COLUMN IF EXISTS legacy_doctor_last_name,
    DROP COLUMN IF EXISTS legacy_doctor_specialty,
    DROP COLUMN IF EXISTS legacy_doctor_certificate,
    DROP COLUMN IF EXISTS legacy_icd10_diagnoses,
    DROP COLUMN IF EXISTS legacy_invoice_type,
    DROP COLUMN IF EXISTS medula_type_value,
    DROP COLUMN IF EXISTS invoice_type,
    DROP COLUMN IF EXISTS medula_qr_status,
    DROP COLUMN IF EXISTS its_finalized,
    DROP COLUMN IF EXISTS prescription_type,
    DROP COLUMN IF EXISTS prescription_subtype,
    DROP COLUMN IF EXISTS provision_type;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='prescription_invoice_type_code_fkey') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT prescription_invoice_type_code_fkey
            FOREIGN KEY (invoice_type_code)
            REFERENCES public.prescription_invoice_type(invoice_type_code);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_prescription_qr_status_code') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT ck_prescription_qr_status_code
            CHECK (qr_status_code BETWEEN 0 AND 3);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_prescription_type_code') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT ck_prescription_type_code
            CHECK (prescription_type_code BETWEEN 1 AND 5);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_prescription_subtype_code') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT ck_prescription_subtype_code
            CHECK (prescription_subtype_code BETWEEN 1 AND 8);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_prescription_provision_type_code') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT ck_prescription_provision_type_code
            CHECK (provision_type_code BETWEEN 1 AND 8);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_prescription_recipient_relationship') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT ck_prescription_recipient_relationship
            CHECK (recipient_relationship IS NULL OR recipient_relationship IN ('0', 'K', 'Y', 'D'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_prescription_list_status_code') THEN
        ALTER TABLE public.prescription ADD CONSTRAINT ck_prescription_list_status_code
            CHECK (list_status_code IS NULL OR list_status_code IN ('I', 'K'));
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS public.icd10_diagnosis (
    code varchar PRIMARY KEY,
    description varchar NOT NULL
);

CREATE TABLE IF NOT EXISTS public.patient_identity (
    tc_identity_no varchar PRIMARY KEY,
    patient_record_id integer NOT NULL REFERENCES public.patient(record_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_prescription_log_uid_version
    ON public.prescription_log (prescription_uid, version_number);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM public.pharmacy_schema_migration
         WHERE migration_id='065_normalize_prescription_codes_finalize') THEN
        INSERT INTO public.patient_identity (tc_identity_no, patient_record_id)
        SELECT DISTINCT ON (BTRIM(tc_identity_no)) BTRIM(tc_identity_no), record_id
          FROM public.patient
         WHERE NULLIF(BTRIM(tc_identity_no), '') IS NOT NULL
         ORDER BY BTRIM(tc_identity_no), record_id DESC
        ON CONFLICT (tc_identity_no) DO NOTHING;

        UPDATE public.prescription
           SET prescription_uid = md5(record_id::text || ':' || clock_timestamp()::text)::uuid
         WHERE prescription_uid IS NULL;
        UPDATE public.prescription
           SET version_number = 1
         WHERE version_number IS NULL OR version_number < 1;
        ALTER TABLE public.prescription ALTER COLUMN prescription_uid SET NOT NULL;
        ALTER TABLE public.prescription ALTER COLUMN version_number SET NOT NULL;

        INSERT INTO public.prescription_log (
            prescription_uid, version_number, action_type, update_datetime, user_id,
            prescription_snapshot)
        SELECT p.prescription_uid, p.version_number,
               CASE WHEN p.version_number = 1 THEN 'FIRST_SAVE' ELSE 'UPDATE' END,
               p.saved_at, p.saved_by_user_id, to_jsonb(p)
          FROM public.prescription p
        ON CONFLICT (prescription_uid, version_number) DO NOTHING;

        DELETE FROM public.prescription older
         USING public.prescription newer
         WHERE older.prescription_uid = newer.prescription_uid
           AND (older.version_number, older.record_id) < (newer.version_number, newer.record_id);

        INSERT INTO public.pharmacy_schema_migration (migration_id)
        VALUES ('065_normalize_prescription_codes_finalize')
        ON CONFLICT (migration_id) DO NOTHING;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_prescription_patient_tc ON public.prescription (patient_tc_identity_no);
CREATE INDEX IF NOT EXISTS ix_prescription_e_prescription_no ON public.prescription (e_prescription_no);
CREATE INDEX IF NOT EXISTS ix_prescription_medula_prescription_no ON public.prescription (medula_prescription_no);
CREATE INDEX IF NOT EXISTS ix_prescription_doctor_registration ON public.prescription (doctor_diploma_registration_no);
CREATE UNIQUE INDEX IF NOT EXISTS ux_prescription_uid_version ON public.prescription (prescription_uid, version_number);
CREATE INDEX IF NOT EXISTS ix_prescription_filter_date ON public.prescription (medula_filter_date);
CREATE INDEX IF NOT EXISTS ix_prescription_identity_fingerprint ON public.prescription (identity_fingerprint);
CREATE UNIQUE INDEX IF NOT EXISTS ux_prescription_uid_current ON public.prescription (prescription_uid);
CREATE INDEX IF NOT EXISTS ix_prescription_log_action_time ON public.prescription_log (update_datetime DESC);
CREATE INDEX IF NOT EXISTS ix_prescription_invoice_type_code ON public.prescription (invoice_type_code);
CREATE INDEX IF NOT EXISTS ix_prescription_qr_status_code ON public.prescription (qr_status_code);
