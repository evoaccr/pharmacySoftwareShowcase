BEGIN;

/*
    This migration intentionally refuses to reshape populated tables. The user
    requested a clean re-import, so empty the six destination tables before
    running it. No source/FARMASSIST data is touched by this script.
*/
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM public.depot LIMIT 1)
       OR EXISTS (SELECT 1 FROM public.doctor LIMIT 1)
       OR EXISTS (SELECT 1 FROM public.pharmacy LIMIT 1)
       OR EXISTS (SELECT 1 FROM public.patient LIMIT 1)
       OR EXISTS (SELECT 1 FROM public.faculty LIMIT 1)
       OR EXISTS (SELECT 1 FROM public.kurum LIMIT 1) THEN
        RAISE EXCEPTION
            'Migration 011 requires depot, doctor, pharmacy, patient, faculty, and kurum to be empty';
    END IF;
END
$$;

/* Drop relationships before replacing the referenced primary keys. */
ALTER TABLE public.patient
    DROP CONSTRAINT IF EXISTS patient_family_doctor_id_fkey,
    DROP CONSTRAINT IF EXISTS patient_kurum_id_fkey;

ALTER TABLE public.doctor
    DROP CONSTRAINT IF EXISTS doctor_facility_no_fkey;

ALTER TABLE public.depot DROP CONSTRAINT IF EXISTS depot_pkey;
ALTER TABLE public.doctor DROP CONSTRAINT IF EXISTS doctor_pkey;
ALTER TABLE public.pharmacy DROP CONSTRAINT IF EXISTS pharmacy_pkey;
ALTER TABLE public.patient DROP CONSTRAINT IF EXISTS patient_pkey;
ALTER TABLE public.faculty DROP CONSTRAINT IF EXISTS faculty_pkey;
ALTER TABLE public.kurum DROP CONSTRAINT IF EXISTS kurum_pkey;

/* Natural identifiers may now be empty; PostgreSQL generates every record_id. */
ALTER TABLE public.depot
    ALTER COLUMN gln_code DROP NOT NULL,
    ADD COLUMN record_id integer GENERATED ALWAYS AS IDENTITY,
    ADD CONSTRAINT depot_pkey PRIMARY KEY (record_id);

ALTER TABLE public.pharmacy
    ALTER COLUMN gln_code DROP NOT NULL,
    ADD COLUMN record_id integer GENERATED ALWAYS AS IDENTITY,
    ADD CONSTRAINT pharmacy_pkey PRIMARY KEY (record_id);

ALTER TABLE public.kurum
    ALTER COLUMN kurum_code DROP NOT NULL,
    ADD COLUMN record_id integer GENERATED ALWAYS AS IDENTITY,
    ADD CONSTRAINT kurum_pkey PRIMARY KEY (record_id);

ALTER TABLE public.faculty
    ALTER COLUMN faculty_code DROP NOT NULL,
    ADD COLUMN record_id integer GENERATED ALWAYS AS IDENTITY,
    ADD CONSTRAINT faculty_pkey PRIMARY KEY (record_id),
    ADD CONSTRAINT uq_faculty_faculty_code UNIQUE (faculty_code);

ALTER TABLE public.doctor
    ALTER COLUMN diploma_registration_no DROP NOT NULL,
    ADD COLUMN record_id integer GENERATED ALWAYS AS IDENTITY,
    ADD CONSTRAINT doctor_pkey PRIMARY KEY (record_id),
    ADD CONSTRAINT uq_doctor_diploma_registration_no UNIQUE (diploma_registration_no);

ALTER TABLE public.patient
    ALTER COLUMN tc_identity_no DROP NOT NULL,
    ALTER COLUMN kurum_id TYPE integer
        USING NULLIF(BTRIM(kurum_id), '')::integer,
    ALTER COLUMN allow_debt SET DEFAULT TRUE,
    ALTER COLUMN debt_amount SET DEFAULT 0.00,
    ADD COLUMN record_id integer GENERATED ALWAYS AS IDENTITY,
    ADD COLUMN "isForeignCitizen" boolean NOT NULL DEFAULT FALSE,
    ADD CONSTRAINT patient_pkey PRIMARY KEY (record_id);

/* Keep the requested natural-code relationships for faculty and doctor. */
ALTER TABLE public.doctor
    ADD CONSTRAINT doctor_facility_no_fkey
    FOREIGN KEY (facility_no)
    REFERENCES public.faculty (faculty_code);

ALTER TABLE public.patient
    ADD CONSTRAINT patient_family_doctor_id_fkey
    FOREIGN KEY (family_doctor_id)
    REFERENCES public.doctor (diploma_registration_no),
    ADD CONSTRAINT patient_kurum_id_fkey
    FOREIGN KEY (kurum_id)
    REFERENCES public.kurum (record_id);

CREATE INDEX ix_patient_kurum_id
    ON public.patient (kurum_id);

/*
    Natural identifiers can be null, so they cannot identify a blank-code row
    on later runs. This internal map keeps imports repeatable without placing a
    FARMASSIST Id into any of the six business tables.
*/
CREATE TABLE public.farmassist_import_map (
    source_table varchar NOT NULL,
    source_id integer NOT NULL,
    target_table varchar NOT NULL,
    target_record_id integer NOT NULL,
    PRIMARY KEY (source_table, source_id),
    UNIQUE (target_table, target_record_id),
    CONSTRAINT ck_farmassist_import_map_target
        CHECK (target_table IN ('depot', 'doctor', 'pharmacy', 'patient', 'faculty', 'kurum'))
);

COMMIT;
