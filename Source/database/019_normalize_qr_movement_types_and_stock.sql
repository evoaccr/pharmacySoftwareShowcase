BEGIN;

CREATE TABLE IF NOT EXISTS public."qrMovementTypes" (
    record_id smallint PRIMARY KEY,
    code varchar NOT NULL UNIQUE,
    display_name varchar NOT NULL,
    is_entry boolean NOT NULL
);

INSERT INTO public."qrMovementTypes" (record_id, code, display_name, is_entry)
VALUES
    (1,  'DEP_IN',        'Depodan Alış',                 TRUE),
    (2,  'DEP_RETURN',    'Depoya İade',                  FALSE),
    (3,  'FIRM_IN',       'Firmadan Alış',                TRUE),
    (4,  'FIRM_RETURN',   'Firmaya İade',                 FALSE),
    (5,  'TRANSF_IN',     'Takas Giriş',                  TRUE),
    (6,  'TRANSF_OUT',    'Takas Çıkış',                  FALSE),
    (7,  'PERAK_OUT',     'Perakende Satış',              FALSE),
    (8,  'PERAK_RETURN',  'Perakende Satış İadesi',       TRUE),
    (9,  'PRESC_OUT',     'Reçeteli Satış',               FALSE),
    (10, 'PRESC_RETURN',  'Reçeteli Satış İadesi',        TRUE),
    (11, 'QUAR_IN',       'Karantinaya Alındı',           FALSE),
    (12, 'QUAR_OUT',      'Karantinadan Çıkarıldı',       TRUE),
    (13, 'DESTRUCTION',   'İmha',                         FALSE),
    (14, 'RECALL',        'Geri Çağırma',                 FALSE),
    (15, 'MAN_IN',        'Manuel Giriş',                 TRUE),
    (16, 'MAN_OUT',       'Manuel Çıkış',                 FALSE),
    (17, 'QR_ADD',        'Karekod Oluşturma',            TRUE),
    (18, 'RESERVED',      'Emanete Ayrıldı',              FALSE),
    (19, 'DAMAGED',       'Hasarlı',                       FALSE)
ON CONFLICT (record_id) DO UPDATE
SET code = EXCLUDED.code,
    display_name = EXCLUDED.display_name,
    is_entry = EXCLUDED.is_entry;

DROP TRIGGER IF EXISTS trg_qrMovements_apply_stock ON public."qrMovements";
DROP TRIGGER IF EXISTS "trg_qrMovements_apply_stock" ON public."qrMovements";

DO $$
DECLARE
    movement_type_data_type text;
BEGIN
    SELECT data_type
    INTO movement_type_data_type
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'qrMovements'
      AND column_name = 'movement_type';

    IF movement_type_data_type IN ('character varying', 'text') THEN
        ALTER TABLE public."qrMovements"
            DROP CONSTRAINT IF EXISTS "ck_qrMovements_movement_type";

        ALTER TABLE public."qrMovements"
            ADD COLUMN movement_type_id smallint;

        UPDATE public."qrMovements" movement
        SET movement_type_id = movement_type.record_id
        FROM public."qrMovementTypes" movement_type
        WHERE movement_type.code = movement.movement_type;

        IF EXISTS (
            SELECT 1 FROM public."qrMovements" WHERE movement_type_id IS NULL
        ) THEN
            RAISE EXCEPTION 'Some QR movements have an unknown movement type.';
        END IF;

        ALTER TABLE public."qrMovements"
            DROP COLUMN movement_type;
        ALTER TABLE public."qrMovements"
            RENAME COLUMN movement_type_id TO movement_type;
        ALTER TABLE public."qrMovements"
            ALTER COLUMN movement_type SET NOT NULL;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'public."qrMovements"'::regclass
          AND conname = 'fk_qrMovements_movement_type'
    ) THEN
        ALTER TABLE public."qrMovements"
            ADD CONSTRAINT "fk_qrMovements_movement_type"
            FOREIGN KEY (movement_type)
            REFERENCES public."qrMovementTypes" (record_id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS "idx_qrMovements_type_date"
    ON public."qrMovements" (movement_type, movement_date DESC);

ALTER TABLE public."qrActive"
    ALTER COLUMN status DROP DEFAULT,
    ALTER COLUMN status DROP NOT NULL;

UPDATE public."qrActive"
SET status = NULL
WHERE status = 'ACTIVE';

ALTER TABLE public."barcodeActive"
    DROP CONSTRAINT IF EXISTS "barcodeActive_barcode_fkey";

INSERT INTO public."barcodeActive" (barcode, quantity)
SELECT barcode, 0
FROM public.barcode
WHERE NULLIF(BTRIM(barcode), '') IS NOT NULL
ON CONFLICT (barcode) DO NOTHING;

INSERT INTO public."barcodeActive" (barcode, quantity, nearest_expiry_date)
SELECT barcode,
       COUNT(*)::integer AS quantity,
       MIN(expiry_date) AS nearest_expiry_date
FROM public."qrActive"
GROUP BY barcode
ON CONFLICT (barcode) DO UPDATE
SET quantity = EXCLUDED.quantity,
    nearest_expiry_date = EXCLUDED.nearest_expiry_date;

CREATE OR REPLACE FUNCTION public.pharmacy_refresh_barcode_active(target_barcode varchar)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    current_quantity integer;
    current_nearest_expiry date;
BEGIN
    SELECT COUNT(*)::integer, MIN(expiry_date)
    INTO current_quantity, current_nearest_expiry
    FROM public."qrActive"
    WHERE barcode = target_barcode;

    INSERT INTO public."barcodeActive" (barcode, quantity, nearest_expiry_date)
    VALUES (target_barcode, current_quantity, current_nearest_expiry)
    ON CONFLICT (barcode) DO UPDATE
    SET quantity = EXCLUDED.quantity,
        nearest_expiry_date = EXCLUDED.nearest_expiry_date;
END;
$$;

CREATE OR REPLACE FUNCTION public.pharmacy_apply_qr_movement()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    active_row public."qrActive"%ROWTYPE;
    inactive_row public."qrInactive"%ROWTYPE;
    movement_code varchar;
    barcode_only varchar;
    parsed_barcode varchar;
    parsed_expiry date;
    inserted_count integer;
BEGIN
    SELECT code INTO STRICT movement_code
    FROM public."qrMovementTypes"
    WHERE record_id = NEW.movement_type;

    SELECT b.barcode INTO barcode_only
    FROM public.barcode b
    WHERE b.barcode = NEW.qr_code
    LIMIT 1;

    IF barcode_only IS NOT NULL THEN
        IF movement_code IN (
            'DEP_IN', 'FIRM_IN', 'TRANSF_IN', 'PERAK_RETURN',
            'PRESC_RETURN', 'MAN_IN', 'QR_ADD'
        ) THEN
            INSERT INTO public."barcodeActive" (barcode, quantity)
            VALUES (barcode_only, 1)
            ON CONFLICT (barcode) DO UPDATE
            SET quantity = public."barcodeActive".quantity + 1;
        ELSIF movement_code IN (
            'DEP_RETURN', 'FIRM_RETURN', 'TRANSF_OUT', 'PERAK_OUT',
            'PRESC_OUT', 'DESTRUCTION', 'MAN_OUT'
        ) THEN
            UPDATE public."barcodeActive"
            SET quantity = quantity - 1
            WHERE barcode = barcode_only
              AND quantity > 0;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'Barkod aktif stokta bulunamadı veya miktarı sıfır: %', barcode_only
                    USING ERRCODE = 'P0002';
            END IF;
        END IF;
        RETURN NEW;
    END IF;

    IF movement_code IN (
        'DEP_IN', 'FIRM_IN', 'TRANSF_IN', 'PERAK_RETURN',
        'PRESC_RETURN', 'MAN_IN', 'QR_ADD'
    ) THEN
        SELECT * INTO inactive_row
        FROM public."qrInactive"
        WHERE qr_code = NEW.qr_code
        FOR UPDATE;

        parsed_barcode := COALESCE(inactive_row.barcode, public.pharmacy_qr_barcode(NEW.qr_code));
        parsed_expiry := COALESCE(inactive_row.expiry_date, public.pharmacy_qr_expiry(NEW.qr_code));
        IF parsed_barcode IS NULL THEN
            RAISE EXCEPTION 'Karekoddan barkod bilgisi okunamadı.'
                USING ERRCODE = '22023';
        END IF;

        INSERT INTO public."qrActive" (
            qr_code, barcode, expiry_date, status, purchase_cost, reference_no
        ) VALUES (
            NEW.qr_code, parsed_barcode, parsed_expiry, NULL,
            inactive_row.entry_cost, NEW.reference_code
        )
        ON CONFLICT (qr_code) DO NOTHING;
        GET DIAGNOSTICS inserted_count = ROW_COUNT;

        IF inserted_count = 0 THEN
            RAISE EXCEPTION 'Karekod zaten aktif stokta: %', NEW.qr_code
                USING ERRCODE = '23505';
        END IF;

        DELETE FROM public."qrInactive" WHERE qr_code = NEW.qr_code;
        PERFORM public.pharmacy_refresh_barcode_active(parsed_barcode);
        RETURN NEW;
    END IF;

    IF movement_code IN (
        'DEP_RETURN', 'FIRM_RETURN', 'TRANSF_OUT', 'PERAK_OUT',
        'PRESC_OUT', 'DESTRUCTION', 'MAN_OUT'
    ) THEN
        SELECT * INTO active_row
        FROM public."qrActive"
        WHERE qr_code = NEW.qr_code
        FOR UPDATE;

        IF NOT FOUND THEN
            RAISE EXCEPTION 'Karekod aktif stokta bulunamadı: %', NEW.qr_code
                USING ERRCODE = 'P0002';
        END IF;

        INSERT INTO public."qrInactive" (
            qr_code, barcode, expiry_date, entry_cost, exit_price, reference_no
        ) VALUES (
            active_row.qr_code, active_row.barcode, active_row.expiry_date,
            active_row.purchase_cost, NULL, NEW.reference_code
        )
        ON CONFLICT (qr_code) DO UPDATE
        SET barcode = EXCLUDED.barcode,
            expiry_date = EXCLUDED.expiry_date,
            entry_cost = EXCLUDED.entry_cost,
            exit_price = EXCLUDED.exit_price,
            reference_no = EXCLUDED.reference_no;

        DELETE FROM public."qrActive" WHERE qr_code = NEW.qr_code;
        PERFORM public.pharmacy_refresh_barcode_active(active_row.barcode);
        RETURN NEW;
    END IF;

    SELECT * INTO active_row
    FROM public."qrActive"
    WHERE qr_code = NEW.qr_code
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Karekod aktif stokta bulunamadı: %', NEW.qr_code
            USING ERRCODE = 'P0002';
    END IF;

    UPDATE public."qrActive"
    SET status = CASE movement_code
        WHEN 'QUAR_IN' THEN 'QUARANTINE'
        WHEN 'QUAR_OUT' THEN NULL
        WHEN 'RECALL' THEN 'RECALLED'
        WHEN 'RESERVED' THEN 'RESERVED'
        WHEN 'DAMAGED' THEN 'DAMAGED'
        ELSE status
    END,
    reference_no = COALESCE(NEW.reference_code, reference_no)
    WHERE qr_code = NEW.qr_code;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_qrMovements_apply_stock
AFTER INSERT ON public."qrMovements"
FOR EACH ROW
EXECUTE FUNCTION public.pharmacy_apply_qr_movement();

COMMIT;
