BEGIN;

-- Non-serialized retail products are valid stock-ledger entries, but a returned
-- barcode cannot identify one historical sale. Keep the refund item and its
-- PERAK_RETURN movement while leaving only that unprovable relationship NULL.
ALTER TABLE public."salesRefundItems"
    ALTER COLUMN original_sale_id DROP NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_salesRefundItems_prescription_original'
    ) THEN
        ALTER TABLE public."salesRefundItems"
            ADD CONSTRAINT "ck_salesRefundItems_prescription_original"
            CHECK (item_source <> 'prescription' OR original_sale_id IS NOT NULL);
    END IF;
END;
$$;

-- Operational Medula cancellation still has one current refund per sale.
-- Historical Farmassist data may contain several partial refunds for the same
-- prescription sale, so imported history is excluded from that uniqueness rule.
ALTER TABLE public."salesRefunds"
    ADD COLUMN IF NOT EXISTS historical_import boolean NOT NULL DEFAULT FALSE;

DROP INDEX IF EXISTS public."ux_salesRefunds_prescription_sale";
DROP INDEX IF EXISTS public.ux_salesrefunds_prescription_sale;

CREATE UNIQUE INDEX ux_salesRefunds_prescription_sale
    ON public."salesRefunds" (original_sale_id)
    WHERE refund_source='prescription' AND NOT historical_import;

CREATE INDEX IF NOT EXISTS ix_salesRefunds_historical_prescription_sale
    ON public."salesRefunds" (original_sale_id, "timestamp")
    WHERE refund_source='prescription' AND historical_import;

-- One Farmassist refund transaction can combine several original
-- prescriptions. Each (source refund, original sale) pair maps to its own PH
-- refund, while separate source refunds for the same original remain separate.
ALTER TABLE public.farmassist_prescription_refund_import_map
    DROP CONSTRAINT IF EXISTS farmassist_prescription_refund_import_map_pkey;

ALTER TABLE public.farmassist_prescription_refund_import_map
    ADD PRIMARY KEY (source_sale_id, source_original_sale_id);

CREATE INDEX IF NOT EXISTS ix_farmassist_prescription_refund_original
    ON public.farmassist_prescription_refund_import_map (source_original_sale_id);

COMMIT;
