BEGIN;

ALTER TABLE public.barcode
    DROP COLUMN IF EXISTS documents,
    ADD COLUMN IF NOT EXISTS all_equivalents varchar[],
    ADD COLUMN IF NOT EXISTS sut_summary jsonb,
    ADD COLUMN IF NOT EXISTS medula_details jsonb,
    ADD COLUMN IF NOT EXISTS medula_info jsonb;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'barcode'
          AND column_name = 'photo'
          AND udt_name = 'bytea'
    ) THEN
        ALTER TABLE public.barcode
            ALTER COLUMN photo TYPE bytea[]
            USING CASE WHEN photo IS NULL THEN NULL ELSE ARRAY[photo] END;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS public.documents (
    barcode varchar PRIMARY KEY,
    kub bytea,
    kit bytea
);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public."barcodePrices"'::regclass
          AND conname = 'barcodePrices_record_reference_no_fkey'
          AND confdeltype <> 'c'
    ) THEN
        ALTER TABLE public."barcodePrices"
            DROP CONSTRAINT "barcodePrices_record_reference_no_fkey";
        ALTER TABLE public."barcodePrices"
            ADD CONSTRAINT "barcodePrices_record_reference_no_fkey"
            FOREIGN KEY (record_reference_no)
            REFERENCES public.barcode (record_id)
            ON DELETE CASCADE;
    END IF;

    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public."barcodePriceHistory"'::regclass
          AND conname = 'barcodePriceHistory_record_reference_no_fkey'
          AND confdeltype <> 'c'
    ) THEN
        ALTER TABLE public."barcodePriceHistory"
            DROP CONSTRAINT "barcodePriceHistory_record_reference_no_fkey";
        ALTER TABLE public."barcodePriceHistory"
            ADD CONSTRAINT "barcodePriceHistory_record_reference_no_fkey"
            FOREIGN KEY (record_reference_no)
            REFERENCES public.barcode (record_id)
            ON DELETE CASCADE;
    END IF;

    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public."barcodeCampaign"'::regclass
          AND conname = 'barcodeCampaign_record_reference_no_fkey'
          AND confdeltype <> 'c'
    ) THEN
        ALTER TABLE public."barcodeCampaign"
            DROP CONSTRAINT "barcodeCampaign_record_reference_no_fkey";
        ALTER TABLE public."barcodeCampaign"
            ADD CONSTRAINT "barcodeCampaign_record_reference_no_fkey"
            FOREIGN KEY (record_reference_no)
            REFERENCES public.barcode (record_id)
            ON DELETE CASCADE;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_barcode_source_product_id
    ON public.barcode (source_product_id);

-- These obsolete empty tables were recreated by the former isolated-table
-- migration chain. The importer now targets only the primary tables above.
DROP TABLE IF EXISTS public."barcodePricesNew";
DROP TABLE IF EXISTS public."barcodePriceHistoryNew";
DROP TABLE IF EXISTS public."barcodeCampaignNew";
DROP TABLE IF EXISTS public."barcodeNew";

COMMIT;
