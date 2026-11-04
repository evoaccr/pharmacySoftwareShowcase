BEGIN;
SET LOCAL lock_timeout = '5s';

ALTER TABLE public.invoice_stock_logs
    ADD COLUMN IF NOT EXISTS movement_type_id integer;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'invoice_stock_logs'
          AND column_name = 'movement_type_code') THEN
        UPDATE public.invoice_stock_logs log
        SET movement_type_id = movement_type.record_id
        FROM public."qrMovementTypes" movement_type
        WHERE log.movement_type_id IS NULL
          AND movement_type.code = log.movement_type_code;
    END IF;
END $$;

UPDATE public.invoice_stock_logs log
SET movement_type_id = movement_type.record_id
FROM public."qrMovementTypes" movement_type
WHERE log.movement_type_id IS NULL
  AND movement_type.code = CASE log.action_code
      WHEN 'SAVE' THEN 'DEP_IN'
      WHEN 'UNSAVE' THEN 'DEP_RETURN'
      WHEN 'REFUND' THEN 'DEP_RETURN'
      WHEN 'REFUND_CANCEL' THEN 'DEP_IN'
      ELSE NULL
  END;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM public.invoice_stock_logs WHERE movement_type_id IS NULL) THEN
        RAISE EXCEPTION 'Invoice stock logs contain an unresolved movement type.';
    END IF;
END $$;

CREATE TEMPORARY TABLE invoice_stock_log_reference_map ON COMMIT DROP AS
SELECT record_id,
       reference_code AS old_reference_code,
       'FI:' || record_id::text AS new_reference_code
FROM public.invoice_stock_logs
WHERE reference_code <> 'FI:' || record_id::text;

UPDATE public."qrMovements" movement
SET reference_code = mapping.new_reference_code
FROM invoice_stock_log_reference_map mapping
WHERE movement.reference_code = mapping.old_reference_code;

UPDATE public."qrActive" active
SET reference_no = mapping.new_reference_code
FROM invoice_stock_log_reference_map mapping
WHERE active.reference_no = mapping.old_reference_code;

UPDATE public."qrInactive" inactive
SET reference_no = mapping.new_reference_code
FROM invoice_stock_log_reference_map mapping
WHERE inactive.reference_no = mapping.old_reference_code;

UPDATE public.invoice_stock_logs log
SET reference_code = mapping.new_reference_code
FROM invoice_stock_log_reference_map mapping
WHERE log.record_id = mapping.record_id;

UPDATE public.invoice_stock_logs
SET notes = ''
WHERE notes = 'Fatura Listesi modülü stok işlemi';

ALTER TABLE public.invoice_stock_logs
    ALTER COLUMN movement_type_id SET NOT NULL,
    ALTER COLUMN notes SET DEFAULT '',
    DROP COLUMN IF EXISTS movement_type_code,
    DROP COLUMN IF EXISTS counterparty_address_id,
    DROP COLUMN IF EXISTS stock_line_count,
    DROP COLUMN IF EXISTS serialized_quantity,
    DROP COLUMN IF EXISTS barcode_quantity,
    DROP COLUMN IF EXISTS movement_count,
    DROP COLUMN IF EXISTS metadata;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public.invoice_stock_logs'::regclass
          AND conname = 'invoice_stock_logs_movement_type_id_fkey') THEN
        ALTER TABLE public.invoice_stock_logs
            ADD CONSTRAINT invoice_stock_logs_movement_type_id_fkey
            FOREIGN KEY (movement_type_id)
            REFERENCES public."qrMovementTypes"(record_id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS invoice_stock_logs_movement_time_idx
    ON public.invoice_stock_logs(movement_type_id, occurred_at DESC, record_id DESC);

COMMIT;
