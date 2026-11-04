BEGIN;

-- Rename ambiguous legacy headers while preserving existing rows.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'drug_name')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'medicine_name') THEN
        ALTER TABLE public.barcode RENAME COLUMN drug_name TO medicine_name;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'product_group')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'product_type_name') THEN
        ALTER TABLE public.barcode RENAME COLUMN product_group TO product_type_name;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'company')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'company_name') THEN
        ALTER TABLE public.barcode RENAME COLUMN company TO company_name;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'equivalent_group')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'equivalent_group_code') THEN
        ALTER TABLE public.barcode RENAME COLUMN equivalent_group TO equivalent_group_code;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'alternative_group')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'equivalent_classification') THEN
        ALTER TABLE public.barcode RENAME COLUMN alternative_group TO equivalent_classification;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'indication')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'indication_summary') THEN
        ALTER TABLE public.barcode RENAME COLUMN indication TO indication_summary;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'dosage')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'dosage_instructions') THEN
        ALTER TABLE public.barcode RENAME COLUMN dosage TO dosage_instructions;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'status')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'market_status') THEN
        ALTER TABLE public.barcode RENAME COLUMN status TO market_status;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'drug_brand')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'brand_name') THEN
        ALTER TABLE public.barcode RENAME COLUMN drug_brand TO brand_name;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'pharmaceutical_dosage_form')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'pharmaceutical_form') THEN
        ALTER TABLE public.barcode RENAME COLUMN pharmaceutical_dosage_form TO pharmaceutical_form;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'company_source_id')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'source_company_id') THEN
        ALTER TABLE public.barcode RENAME COLUMN company_source_id TO source_company_id;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'warnings_data')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'warnings') THEN
        ALTER TABLE public.barcode RENAME COLUMN warnings_data TO warnings;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'sut_data')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'sut_rules') THEN
        ALTER TABLE public.barcode RENAME COLUMN sut_data TO sut_rules;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'documents_data')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'documents') THEN
        ALTER TABLE public.barcode RENAME COLUMN documents_data TO documents;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'indications_data')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'indications') THEN
        ALTER TABLE public.barcode RENAME COLUMN indications_data TO indications;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'source_product_data')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'source_product') THEN
        ALTER TABLE public.barcode RENAME COLUMN source_product_data TO source_product;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'source_package_data')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcode' AND column_name = 'source_package') THEN
        ALTER TABLE public.barcode RENAME COLUMN source_package_data TO source_package;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcodePrices' AND column_name = 'current_campaign')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcodePrices' AND column_name = 'current_campaigns') THEN
        ALTER TABLE public."barcodePrices" RENAME COLUMN current_campaign TO current_campaigns;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcodePrices' AND column_name = 'best_campaign')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcodePrices' AND column_name = 'best_campaigns') THEN
        ALTER TABLE public."barcodePrices" RENAME COLUMN best_campaign TO best_campaigns;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcodePrices' AND column_name = 'campaign_period')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcodePrices' AND column_name = 'campaign_periods') THEN
        ALTER TABLE public."barcodePrices" RENAME COLUMN campaign_period TO campaign_periods;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcodePrices' AND column_name = 'source_price_data')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcodePrices' AND column_name = 'source_current_price') THEN
        ALTER TABLE public."barcodePrices" RENAME COLUMN source_price_data TO source_current_price;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcodePrices' AND column_name = 'source_price_history_data')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'barcodePrices' AND column_name = 'source_price_history') THEN
        ALTER TABLE public."barcodePrices" RENAME COLUMN source_price_history_data TO source_price_history;
    END IF;
END $$;

ALTER TABLE public.barcode
    ADD COLUMN IF NOT EXISTS product_type_id integer,
    ADD COLUMN IF NOT EXISTS product_form_code varchar,
    ADD COLUMN IF NOT EXISTS prescription_type_name varchar,
    ADD COLUMN IF NOT EXISTS generic_name varchar,
    ADD COLUMN IF NOT EXISTS reimbursement_status_code integer,
    ADD COLUMN IF NOT EXISTS reimbursement_status_description text,
    ADD COLUMN IF NOT EXISTS sut_summary_code varchar,
    ADD COLUMN IF NOT EXISTS reference_price_group varchar,
    ADD COLUMN IF NOT EXISTS is_ek4f boolean,
    ADD COLUMN IF NOT EXISTS twenty_year_status boolean;

ALTER TABLE public."barcodePrices"
    ADD COLUMN IF NOT EXISTS currency varchar,
    ADD COLUMN IF NOT EXISTS price_effective_date varchar,
    ADD COLUMN IF NOT EXISTS price_type varchar;

-- These source values are numeric in TEBRP. Convert the old varchar columns safely.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'barcode'
          AND column_name = 'source_company_id' AND data_type <> 'integer'
    ) THEN
        ALTER TABLE public.barcode
            ALTER COLUMN source_company_id TYPE integer
            USING CASE
                WHEN source_company_id::text ~ '^[0-9]+$' THEN source_company_id::integer
                ELSE NULL
            END;
    END IF;
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'barcode'
          AND column_name = 'market_status' AND data_type <> 'integer'
    ) THEN
        ALTER TABLE public.barcode
            ALTER COLUMN market_status TYPE integer
            USING CASE
                WHEN market_status::text ~ '^-?[0-9]+$' THEN market_status::integer
                ELSE NULL
            END;
    END IF;
END $$;

-- PostgreSQL arrays cannot represent [date, [six prices]] because arrays must be
-- rectangular. JSONB preserves the exact nesting required by the format files.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'barcodePrices'
          AND column_name = 'campaign_periods' AND data_type <> 'jsonb'
    ) THEN
        ALTER TABLE public."barcodePrices"
            ALTER COLUMN campaign_periods TYPE jsonb
            USING CASE WHEN campaign_periods IS NULL THEN NULL ELSE to_jsonb(campaign_periods) END;
    END IF;
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'barcodePrices'
          AND column_name = 'price_history' AND data_type <> 'jsonb'
    ) THEN
        ALTER TABLE public."barcodePrices"
            ALTER COLUMN price_history TYPE jsonb
            USING CASE WHEN price_history IS NULL THEN NULL ELSE to_jsonb(price_history) END;
    END IF;
END $$;

-- Migration 004 validates and moves the already-canonical compact history into
-- the relational "barcodePriceHistory" table before dropping legacy sources.

-- The text versions below duplicated the canonical JSONB endpoint payloads.
ALTER TABLE public.barcode
    DROP COLUMN IF EXISTS label,
    DROP COLUMN IF EXISTS warning,
    DROP COLUMN IF EXISTS prospectus,
    DROP COLUMN IF EXISTS sut,
    DROP COLUMN IF EXISTS drug_brand,
    DROP COLUMN IF EXISTS pharmaceutical_dosage_form,
    DROP COLUMN IF EXISTS company_source_id,
    DROP COLUMN IF EXISTS warnings_data,
    DROP COLUMN IF EXISTS sut_data,
    DROP COLUMN IF EXISTS documents_data,
    DROP COLUMN IF EXISTS indications_data,
    DROP COLUMN IF EXISTS source_product_data,
    DROP COLUMN IF EXISTS source_package_data;

ALTER TABLE public."barcodePrices"
    DROP COLUMN IF EXISTS source_price_data,
    DROP COLUMN IF EXISTS source_price_history_data;

-- Previous importer versions created a column for every raw key. The same data
-- is already retained in source_product/source_package/source_current_price,
-- so remove those duplicate, non-descriptive columns.
DO $$
DECLARE
    duplicate_column text;
BEGIN
    FOR duplicate_column IN
        SELECT column_name
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'barcode'
          AND column_name LIKE 'tebrp\_%' ESCAPE '\'
    LOOP
        EXECUTE format('ALTER TABLE public.barcode DROP COLUMN %I', duplicate_column);
    END LOOP;

    FOR duplicate_column IN
        SELECT column_name
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'barcodePrices'
          AND column_name LIKE 'tebrp\_%' ESCAPE '\'
    LOOP
        EXECUTE format('ALTER TABLE public."barcodePrices" DROP COLUMN %I', duplicate_column);
    END LOOP;
END $$;

COMMIT;
