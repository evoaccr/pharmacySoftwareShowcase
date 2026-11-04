BEGIN;

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

COMMIT;
