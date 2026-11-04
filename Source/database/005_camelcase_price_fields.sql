BEGIN;

LOCK TABLE public.barcode IN ACCESS EXCLUSIVE MODE;
LOCK TABLE public."barcodePrices" IN ACCESS EXCLUSIVE MODE;

ALTER TABLE public."barcodePrices"
    ADD COLUMN IF NOT EXISTS public_paid_price numeric,
    ADD COLUMN IF NOT EXISTS price_difference numeric;

-- kfb_string order from TEBRP: KF, Kİ, KÖ, FF, currency.
DO $$
DECLARE
    mismatch_count bigint;
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'barcode'
          AND column_name = 'source_product'
    ) THEN
        UPDATE public."barcodePrices" bp
        SET sale_price = COALESCE(
                bp.sale_price,
                CASE WHEN split_part(b.source_product ->> 'tfb_string', '#', 4)
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(split_part(b.source_product ->> 'tfb_string', '#', 4), ',', '.')::numeric END
            ),
            warehouse_price = COALESCE(
                bp.warehouse_price,
                CASE WHEN split_part(b.source_product ->> 'tfb_string', '#', 1)
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(split_part(b.source_product ->> 'tfb_string', '#', 1), ',', '.')::numeric END
            ),
            manufacturer_price = COALESCE(
                bp.manufacturer_price,
                CASE WHEN split_part(b.source_product ->> 'tfb_string', '#', 3)
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(split_part(b.source_product ->> 'tfb_string', '#', 3), ',', '.')::numeric END
            ),
            public_price = COALESCE(
                bp.public_price,
                CASE WHEN split_part(b.source_product ->> 'kfb_string', '#', 1)
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(split_part(b.source_product ->> 'kfb_string', '#', 1), ',', '.')::numeric END
            ),
            discount_rate = COALESCE(
                bp.discount_rate,
                CASE WHEN split_part(b.source_product ->> 'kfb_string', '#', 2)
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(split_part(b.source_product ->> 'kfb_string', '#', 2), ',', '.')::numeric END
            ),
            public_paid_price = COALESCE(
                bp.public_paid_price,
                CASE
                    WHEN split_part(b.source_product ->> 'kfb_string', '#', 3)
                         ~ '^-?[0-9]+([.,][0-9]+)?$'
                    THEN replace(
                        split_part(b.source_product ->> 'kfb_string', '#', 3), ',', '.'
                    )::numeric
                END
            ),
            price_difference = COALESCE(
                bp.price_difference,
                CASE
                    WHEN split_part(b.source_product ->> 'kfb_string', '#', 4)
                         ~ '^-?[0-9]+([.,][0-9]+)?$'
                    THEN replace(
                        split_part(b.source_product ->> 'kfb_string', '#', 4), ',', '.'
                    )::numeric
                END
            )
        FROM public.barcode b
        WHERE b.record_id = bp.record_reference_no;

        SELECT count(*) INTO mismatch_count
        FROM public."barcodePrices" bp
        JOIN public.barcode b ON b.record_id = bp.record_reference_no
        WHERE (
                split_part(b.source_product ->> 'kfb_string', '#', 3)
                    ~ '^-?[0-9]+([.,][0-9]+)?$'
                AND bp.public_paid_price IS DISTINCT FROM replace(
                    split_part(b.source_product ->> 'kfb_string', '#', 3), ',', '.'
                )::numeric
              )
           OR (
                split_part(b.source_product ->> 'kfb_string', '#', 4)
                    ~ '^-?[0-9]+([.,][0-9]+)?$'
                AND bp.price_difference IS DISTINCT FROM replace(
                    split_part(b.source_product ->> 'kfb_string', '#', 4), ',', '.'
                )::numeric
              );
        IF mismatch_count <> 0 THEN
            RAISE EXCEPTION 'Current KÖ/FF migration failed for % row(s)', mismatch_count;
        END IF;
    END IF;
END $$;

ALTER TABLE public."barcodePriceHistory"
    ADD COLUMN IF NOT EXISTS public_paid_price numeric(12,2),
    ADD COLUMN IF NOT EXISTS price_difference numeric(12,2);

-- On installations that still have raw history, rebuild affected products from
-- the website fields so KF, KÖ, FF, and Kİ remain distinct.
DO $$
DECLARE
    invalid_count bigint;
    expected_count bigint;
    actual_count bigint;
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'barcodePrices'
          AND column_name = 'source_price_history'
    ) THEN
        EXECUTE $validate$
            SELECT count(*)
            FROM public."barcodePrices" bp
            CROSS JOIN LATERAL jsonb_array_elements(
                CASE
                    WHEN jsonb_typeof(bp.source_price_history) = 'array'
                    THEN bp.source_price_history
                    ELSE '[]'::jsonb
                END
            ) AS entries(item)
            WHERE NULLIF(item ->> 'gecerlilik_tarihi', '') IS NULL
               OR NOT (
                    ((item ->> 'gecerlilik_tarihi') ~ '^[0-9]{2}/[0-9]{2}/[0-9]{4}$'
                     AND to_char(to_date(item ->> 'gecerlilik_tarihi', 'DD/MM/YYYY'), 'DD/MM/YYYY')
                         = item ->> 'gecerlilik_tarihi')
                    OR
                    ((item ->> 'gecerlilik_tarihi') ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
                     AND to_char(to_date(item ->> 'gecerlilik_tarihi', 'YYYY-MM-DD'), 'YYYY-MM-DD')
                         = item ->> 'gecerlilik_tarihi')
               )
        $validate$ INTO invalid_count;
        IF invalid_count <> 0 THEN
            RAISE EXCEPTION 'Raw price history contains % invalid date(s)', invalid_count;
        END IF;

        EXECUTE $count_expected$
            SELECT COALESCE(sum(jsonb_array_length(source_price_history)), 0)
            FROM public."barcodePrices"
            WHERE jsonb_typeof(source_price_history) = 'array'
        $count_expected$ INTO expected_count;

        EXECUTE $delete_old$
            DELETE FROM public."barcodePriceHistory" h
            USING public."barcodePrices" bp
            WHERE h.record_reference_no = bp.record_reference_no
              AND jsonb_typeof(bp.source_price_history) = 'array'
        $delete_old$;

        EXECUTE $insert_raw$
            INSERT INTO public."barcodePriceHistory" (
                record_reference_no, effective_date, sale_price, public_price,
                public_paid_price, price_difference, vat_rate, discount_rate,
                warehouse_price, manufacturer_price
            )
            SELECT
                bp.record_reference_no,
                CASE
                    WHEN item ->> 'gecerlilik_tarihi' ~ '^[0-9]{2}/[0-9]{2}/[0-9]{4}$'
                    THEN to_date(item ->> 'gecerlilik_tarihi', 'DD/MM/YYYY')
                    ELSE to_date(item ->> 'gecerlilik_tarihi', 'YYYY-MM-DD')
                END,
                CASE WHEN COALESCE(NULLIF(item ->> 'perakende_satis_fiyati', ''),
                                  split_part(item ->> 'fiyat_detay', '#', 5))
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(COALESCE(NULLIF(item ->> 'perakende_satis_fiyati', ''),
                                          split_part(item ->> 'fiyat_detay', '#', 5)), ',', '.')::numeric(12,2) END,
                CASE WHEN split_part(item ->> 'kamu_fiyat_detay', '#', 1)
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(split_part(item ->> 'kamu_fiyat_detay', '#', 1), ',', '.')::numeric(12,2) END,
                CASE WHEN COALESCE(NULLIF(item ->> 'kamu_odenen', ''),
                                  split_part(item ->> 'kamu_fiyat_detay', '#', 3))
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(COALESCE(NULLIF(item ->> 'kamu_odenen', ''),
                                          split_part(item ->> 'kamu_fiyat_detay', '#', 3)), ',', '.')::numeric(12,2) END,
                CASE WHEN split_part(item ->> 'kamu_fiyat_detay', '#', 4)
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(split_part(item ->> 'kamu_fiyat_detay', '#', 4), ',', '.')::numeric(12,2) END,
                CASE WHEN split_part(item ->> 'fiyat_detay', '#', 8)
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(split_part(item ->> 'fiyat_detay', '#', 8), ',', '.')::numeric(5,2) END,
                CASE WHEN COALESCE(NULLIF(item ->> 'iskonto', ''),
                                  split_part(item ->> 'kamu_fiyat_detay', '#', 2))
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(COALESCE(NULLIF(item ->> 'iskonto', ''),
                                          split_part(item ->> 'kamu_fiyat_detay', '#', 2)), ',', '.')::numeric(5,2) END,
                CASE WHEN split_part(item ->> 'fiyat_detay', '#', 6)
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(split_part(item ->> 'fiyat_detay', '#', 6), ',', '.')::numeric(12,2) END,
                CASE WHEN split_part(item ->> 'fiyat_detay', '#', 7)
                               ~ '^-?[0-9]+([.,][0-9]+)?$'
                     THEN replace(split_part(item ->> 'fiyat_detay', '#', 7), ',', '.')::numeric(12,2) END
            FROM public."barcodePrices" bp
            CROSS JOIN LATERAL jsonb_array_elements(bp.source_price_history) AS entries(item)
            WHERE jsonb_typeof(bp.source_price_history) = 'array'
        $insert_raw$;

        EXECUTE $count_actual$
            SELECT count(*)
            FROM public."barcodePriceHistory" h
            JOIN public."barcodePrices" bp
              ON bp.record_reference_no = h.record_reference_no
            WHERE jsonb_typeof(bp.source_price_history) = 'array'
        $count_actual$ INTO actual_count;
        IF actual_count <> expected_count THEN
            RAISE EXCEPTION 'Full price-history migration mismatch: expected %, stored %',
                expected_count, actual_count;
        END IF;
    END IF;
END $$;

-- Upgrade databases on which migration 004 had already created snake-case
-- relational tables. Existing public_price in that old table represented KÖ.
DO $$
BEGIN
    IF to_regclass('public.barcode_price_history') IS NOT NULL THEN
        EXECUTE $copy_old_history$
            INSERT INTO public."barcodePriceHistory" (
                record_reference_no, effective_date, sale_price, public_price,
                public_paid_price, price_difference, vat_rate, discount_rate,
                warehouse_price, manufacturer_price
            )
            SELECT
                old.record_reference_no, old.effective_date, old.sale_price, NULL,
                old.public_price, NULL, old.vat_rate, old.discount_rate,
                old.warehouse_price, old.manufacturer_price
            FROM public.barcode_price_history old
            WHERE NOT EXISTS (
                SELECT 1 FROM public."barcodePriceHistory" current
                WHERE current.record_reference_no = old.record_reference_no
            )
        $copy_old_history$;
        EXECUTE 'DROP TABLE public.barcode_price_history';
    END IF;
END $$;

DO $$
BEGIN
    IF to_regclass('public.barcode_campaign') IS NOT NULL THEN
        EXECUTE $copy_old_campaign$
            INSERT INTO public."barcodeCampaign" (
                record_reference_no, warehouse_gln, purchase_quantity,
                bonus_quantity, period_days, start_date, end_date
            )
            SELECT
                old.record_reference_no, old.warehouse_gln, old.purchase_quantity,
                old.bonus_quantity, old.period_days, old.start_date, old.end_date
            FROM public.barcode_campaign old
            WHERE NOT EXISTS (
                SELECT 1 FROM public."barcodeCampaign" current
                WHERE current.record_reference_no = old.record_reference_no
                  AND current.warehouse_gln = old.warehouse_gln
                  AND current.purchase_quantity = old.purchase_quantity
                  AND current.bonus_quantity = old.bonus_quantity
                  AND current.period_days IS NOT DISTINCT FROM old.period_days
                  AND current.start_date IS NOT DISTINCT FROM old.start_date
                  AND current.end_date IS NOT DISTINCT FROM old.end_date
            )
        $copy_old_campaign$;
        EXECUTE 'DROP TABLE public.barcode_campaign';
    END IF;
END $$;

ALTER TABLE public.barcode
    DROP COLUMN IF EXISTS package_quantity,
    DROP COLUMN IF EXISTS source_product,
    DROP COLUMN IF EXISTS source_package,
    DROP COLUMN IF EXISTS equivalent_products;

ALTER TABLE public."barcodePrices"
    DROP COLUMN IF EXISTS current_campaigns,
    DROP COLUMN IF EXISTS best_campaigns,
    DROP COLUMN IF EXISTS campaign_periods,
    DROP COLUMN IF EXISTS price_history,
    DROP COLUMN IF EXISTS source_current_price,
    DROP COLUMN IF EXISTS source_price_history,
    DROP COLUMN IF EXISTS discounts;

COMMIT;
