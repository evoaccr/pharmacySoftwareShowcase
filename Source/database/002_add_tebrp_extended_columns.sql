BEGIN;

ALTER TABLE public.barcode
    ADD COLUMN IF NOT EXISTS source_product_id integer,
    ADD COLUMN IF NOT EXISTS drug_brand varchar,
    ADD COLUMN IF NOT EXISTS package_description varchar,
    ADD COLUMN IF NOT EXISTS pharmaceutical_dosage_form varchar,
    ADD COLUMN IF NOT EXISTS prescription_type_id integer,
    ADD COLUMN IF NOT EXISTS license_number varchar,
    ADD COLUMN IF NOT EXISTS license_date varchar,
    ADD COLUMN IF NOT EXISTS shelf_life varchar,
    ADD COLUMN IF NOT EXISTS generic_code varchar,
    ADD COLUMN IF NOT EXISTS nfc_code varchar,
    ADD COLUMN IF NOT EXISTS company_source_id varchar,
    ADD COLUMN IF NOT EXISTS pregnancy_category varchar,
    ADD COLUMN IF NOT EXISTS old_name text,
    ADD COLUMN IF NOT EXISTS free_text text,
    ADD COLUMN IF NOT EXISTS warnings jsonb,
    ADD COLUMN IF NOT EXISTS sut_rules jsonb,
    ADD COLUMN IF NOT EXISTS documents jsonb,
    ADD COLUMN IF NOT EXISTS indications jsonb;

COMMIT;
