BEGIN;

-- Isolated TEBRP import tables. The existing barcode tables and their rows are
-- intentionally not altered or copied.
CREATE TABLE IF NOT EXISTS public."barcodeNew" (
    LIKE public.barcode INCLUDING ALL
);

ALTER TABLE public."barcodeNew"
    ADD COLUMN IF NOT EXISTS all_equivalents varchar[],
    ADD COLUMN IF NOT EXISTS sut_summary jsonb,
    DROP COLUMN IF EXISTS documents;

CREATE TABLE IF NOT EXISTS public."barcodePricesNew" (
    LIKE public."barcodePrices" INCLUDING ALL
);

CREATE TABLE IF NOT EXISTS public."barcodePriceHistoryNew" (
    LIKE public."barcodePriceHistory" INCLUDING ALL
);

CREATE TABLE IF NOT EXISTS public."barcodeCampaignNew" (
    LIKE public."barcodeCampaign" INCLUDING ALL
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'barcodePricesNew_record_reference_fk'
          AND conrelid = 'public."barcodePricesNew"'::regclass
    ) THEN
        ALTER TABLE public."barcodePricesNew"
            ADD CONSTRAINT "barcodePricesNew_record_reference_fk"
            FOREIGN KEY (record_reference_no)
            REFERENCES public."barcodeNew" (record_id)
            ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'barcodePriceHistoryNew_record_reference_fk'
          AND conrelid = 'public."barcodePriceHistoryNew"'::regclass
    ) THEN
        ALTER TABLE public."barcodePriceHistoryNew"
            ADD CONSTRAINT "barcodePriceHistoryNew_record_reference_fk"
            FOREIGN KEY (record_reference_no)
            REFERENCES public."barcodeNew" (record_id)
            ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'barcodeCampaignNew_record_reference_fk'
          AND conrelid = 'public."barcodeCampaignNew"'::regclass
    ) THEN
        ALTER TABLE public."barcodeCampaignNew"
            ADD CONSTRAINT "barcodeCampaignNew_record_reference_fk"
            FOREIGN KEY (record_reference_no)
            REFERENCES public."barcodeNew" (record_id)
            ON DELETE CASCADE;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS "idx_barcodeNew_source_product_id"
    ON public."barcodeNew" (source_product_id);
CREATE INDEX IF NOT EXISTS "idx_barcodeNew_barcode"
    ON public."barcodeNew" (barcode);
CREATE TABLE IF NOT EXISTS public.documents (
    barcode varchar PRIMARY KEY,
    kub bytea,
    kit bytea
);

COMMIT;
