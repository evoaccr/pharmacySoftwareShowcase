BEGIN;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'faculty'
          AND column_name = 'faculty_id'
    ) AND NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'faculty'
          AND column_name = 'faculty_code'
    ) THEN
        ALTER TABLE public.faculty RENAME COLUMN faculty_id TO faculty_code;
    END IF;
END
$$;

ALTER TABLE public.faculty
    ALTER COLUMN faculty_code TYPE varchar USING faculty_code::varchar,
    ADD COLUMN IF NOT EXISTS address text,
    ADD COLUMN IF NOT EXISTS city varchar,
    ADD COLUMN IF NOT EXISTS district varchar,
    ADD COLUMN IF NOT EXISTS country varchar,
    ADD COLUMN IF NOT EXISTS ownership_type varchar,
    ADD COLUMN IF NOT EXISTS care_level integer,
    ADD COLUMN IF NOT EXISTS tax_no varchar,
    ADD COLUMN IF NOT EXISTS tax_office varchar,
    ADD COLUMN IF NOT EXISTS phone_numbers varchar[];

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'kurum'
          AND column_name = 'kurum_id'
    ) AND NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'kurum'
          AND column_name = 'kurum_code'
    ) THEN
        ALTER TABLE public.kurum RENAME COLUMN kurum_id TO kurum_code;
    END IF;
END
$$;

ALTER TABLE public.kurum
    ALTER COLUMN kurum_code TYPE varchar USING kurum_code::varchar,
    ADD COLUMN IF NOT EXISTS contact_person varchar,
    ADD COLUMN IF NOT EXISTS address text,
    ADD COLUMN IF NOT EXISTS city varchar,
    ADD COLUMN IF NOT EXISTS district varchar,
    ADD COLUMN IF NOT EXISTS phone_numbers varchar[],
    ADD COLUMN IF NOT EXISTS email_addresses varchar[],
    ADD COLUMN IF NOT EXISTS website varchar,
    ADD COLUMN IF NOT EXISTS gln_code varchar,
    ADD COLUMN IF NOT EXISTS tax_no varchar,
    ADD COLUMN IF NOT EXISTS tax_office varchar;

ALTER TABLE public.doctor
    ADD COLUMN IF NOT EXISTS phone_number varchar,
    ADD COLUMN IF NOT EXISTS email_address varchar,
    ADD COLUMN IF NOT EXISTS tc_identity_no varchar;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'doctor_facility_no_fkey'
          AND conrelid = 'public.doctor'::regclass
    ) THEN
        ALTER TABLE public.doctor
            ADD CONSTRAINT doctor_facility_no_fkey
            FOREIGN KEY (facility_no)
            REFERENCES public.faculty (faculty_code);
    END IF;
END
$$;

ALTER TABLE public.pharmacy
    ADD COLUMN IF NOT EXISTS address text,
    ADD COLUMN IF NOT EXISTS district varchar,
    ADD COLUMN IF NOT EXISTS city varchar,
    ADD COLUMN IF NOT EXISTS postal_code varchar,
    ADD COLUMN IF NOT EXISTS phone_numbers varchar[],
    ADD COLUMN IF NOT EXISTS fax_numbers varchar[],
    ADD COLUMN IF NOT EXISTS email_addresses varchar[],
    ADD COLUMN IF NOT EXISTS tax_office varchar,
    ADD COLUMN IF NOT EXISTS tax_no varchar,
    ADD COLUMN IF NOT EXISTS uts_company_numbers varchar[];

ALTER TABLE public.depot
    ADD COLUMN IF NOT EXISTS contact_person varchar,
    ADD COLUMN IF NOT EXISTS e_invoice_username varchar,
    ADD COLUMN IF NOT EXISTS e_invoice_password varchar,
    ADD COLUMN IF NOT EXISTS e_invoice_region_name varchar,
    ADD COLUMN IF NOT EXISTS address text,
    ADD COLUMN IF NOT EXISTS city varchar,
    ADD COLUMN IF NOT EXISTS district varchar,
    ADD COLUMN IF NOT EXISTS postal_code varchar,
    ADD COLUMN IF NOT EXISTS business_phone_numbers varchar[],
    ADD COLUMN IF NOT EXISTS fax_numbers varchar[],
    ADD COLUMN IF NOT EXISTS email_addresses varchar[],
    ADD COLUMN IF NOT EXISTS website varchar,
    ADD COLUMN IF NOT EXISTS tax_no varchar,
    ADD COLUMN IF NOT EXISTS tax_office varchar,
    ADD COLUMN IF NOT EXISTS is_active boolean,
    ADD COLUMN IF NOT EXISTS uts_company_numbers varchar[],
    ADD COLUMN IF NOT EXISTS web_username varchar,
    ADD COLUMN IF NOT EXISTS web_password varchar;

CREATE TABLE IF NOT EXISTS public.patient (
    tc_identity_no varchar PRIMARY KEY,
    first_name varchar,
    last_name varchar,
    relationship_group_code varchar,
    family_doctor_id varchar,
    kurum_id integer,
    note text,
    address text,
    phone_numbers varchar[],
    email_addresses varchar[],
    debt_amount numeric,
    allow_debt boolean,
    maximum_debt numeric,
    registration_no varchar,
    date_of_birth date,
    photo bytea,
    gender varchar,
    debt_credit_score numeric,
    alive boolean DEFAULT TRUE,
    consigned_qr_codes varchar[],
    patient_type varchar,
    insurance_type varchar,
    coverage_type varchar,
    medication_history varchar[],
    examination_history varchar[],
    CONSTRAINT patient_family_doctor_id_fkey
        FOREIGN KEY (family_doctor_id)
        REFERENCES public.doctor (diploma_registration_no)
);

COMMIT;
