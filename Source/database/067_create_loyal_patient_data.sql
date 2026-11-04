CREATE TABLE IF NOT EXISTS public.pharmacy_schema_migration (
    migration_id varchar PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.patient
    ADD COLUMN IF NOT EXISTS is_loyal boolean NOT NULL DEFAULT false;

CREATE INDEX IF NOT EXISTS ix_patient_is_loyal
    ON public.patient(is_loyal) WHERE is_loyal;

CREATE TABLE IF NOT EXISTS public.loyal_patient_profile (
    patient_record_id integer PRIMARY KEY
        REFERENCES public.patient(record_id) ON DELETE CASCADE,
    medication_history_synced_at timestamptz,
    report_history_synced_at timestamptz,
    last_sync_error text
);

CREATE TABLE IF NOT EXISTS public.loyal_patient_medication_history (
    record_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    patient_record_id integer NOT NULL
        REFERENCES public.loyal_patient_profile(patient_record_id) ON DELETE CASCADE,
    prescription_no varchar,
    medication_barcode varchar NOT NULL,
    medication_purchase_date date,
    available_date date,
    quantity numeric,
    medication_usage text,
    report_diagnosis_no text,
    synced_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_loyal_patient_medication_history_patient
    ON public.loyal_patient_medication_history(patient_record_id, medication_purchase_date DESC);
CREATE INDEX IF NOT EXISTS ix_loyal_patient_medication_history_barcode
    ON public.loyal_patient_medication_history(medication_barcode);

CREATE TABLE IF NOT EXISTS public.loyal_patient_report_history (
    record_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    patient_record_id integer NOT NULL
        REFERENCES public.loyal_patient_profile(patient_record_id) ON DELETE CASCADE,
    report_tracking_no varchar,
    report_no varchar,
    start_date date,
    end_date date,
    diagnosis text,
    synced_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_loyal_patient_report_history_patient
    ON public.loyal_patient_report_history(patient_record_id, start_date DESC);

INSERT INTO public.pharmacy_schema_migration(migration_id)
VALUES ('067_create_loyal_patient_data')
ON CONFLICT (migration_id) DO NOTHING;
