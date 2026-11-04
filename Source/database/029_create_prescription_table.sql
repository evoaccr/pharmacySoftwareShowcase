BEGIN;

CREATE TABLE IF NOT EXISTS public.prescription (
    record_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    e_prescription_no varchar,
    prescription_type varchar,
    prescription_subtype varchar,
    invoice_type varchar,
    provision_type varchar,
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
    its_finalized boolean
);

/* Keep upgrades idempotent when an earlier partial table is already present. */
ALTER TABLE public.prescription
    ADD COLUMN IF NOT EXISTS e_prescription_no varchar,
    ADD COLUMN IF NOT EXISTS prescription_type varchar,
    ADD COLUMN IF NOT EXISTS prescription_subtype varchar,
    ADD COLUMN IF NOT EXISTS invoice_type varchar,
    ADD COLUMN IF NOT EXISTS provision_type varchar,
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
    ADD COLUMN IF NOT EXISTS its_finalized boolean;

CREATE INDEX IF NOT EXISTS ix_prescription_patient_tc
    ON public.prescription (patient_tc_identity_no);
CREATE INDEX IF NOT EXISTS ix_prescription_e_prescription_no
    ON public.prescription (e_prescription_no);
CREATE INDEX IF NOT EXISTS ix_prescription_doctor_registration
    ON public.prescription (doctor_diploma_registration_no);

COMMIT;
