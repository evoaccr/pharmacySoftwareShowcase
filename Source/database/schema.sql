--
-- PostgreSQL database dump
--

\restrict tY6YV5qaReYgmUChQfTgKi5brOA87WBtmasW0bNzIByarhDohxfUaqgfdgjF23b

-- Dumped from database version 18.4
-- Dumped by pg_dump version 18.4

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: adjust_patient_prescription_count(character varying, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.adjust_patient_prescription_count(identity_no character varying, adjustment integer) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF identity_no IS NULL OR adjustment = 0 THEN
        RETURN;
    END IF;

    UPDATE public.patient patient
       SET prescription_count=GREATEST(patient.prescription_count + adjustment, 0)
      FROM public.patient_identity identity_row
     WHERE identity_row.tc_identity_no=identity_no
       AND patient.record_id=identity_row.patient_record_id;
END $$;


--
-- Name: audit_minor_card_change(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.audit_minor_card_change() RETURNS trigger
    LANGUAGE plpgsql
    AS $_$
DECLARE
    old_json jsonb := CASE WHEN TG_OP='INSERT' THEN NULL ELSE to_jsonb(OLD) END;
    new_json jsonb := CASE WHEN TG_OP='DELETE' THEN NULL ELSE to_jsonb(NEW) END;
    row_json jsonb := COALESCE(new_json, old_json);
    key_column text := TG_ARGV[1];
    audit_user text;
    audit_time timestamptz := clock_timestamp();
    audit_id bigint;
    old_debt numeric := COALESCE((old_json->>'debt_amount')::numeric, 0);
    new_debt numeric := COALESCE((new_json->>'debt_amount')::numeric, 0);
    debt_delta numeric;
    debt_reference text;
BEGIN
    audit_user := COALESCE(
        NULLIF(current_setting('pharmacy.user_id', true), ''),
        NULLIF(regexp_replace(current_setting('application_name', true), '^pharmacyApp:', ''), current_setting('application_name', true)),
        session_user);

    EXECUTE format('INSERT INTO public.%I(record_key, operation, old_data, new_data, user_id, "timestamp") VALUES ($1,$2,$3,$4,$5,$6) RETURNING log_id', TG_ARGV[0])
    INTO audit_id USING row_json->>key_column, TG_OP, old_json, new_json, audit_user, audit_time;

    IF TG_TABLE_NAME='patient' THEN
        debt_delta := new_debt-old_debt;
        IF debt_delta <> 0 THEN
            debt_reference := COALESCE(NULLIF(current_setting('pharmacy.reference_code', true), ''), 'H' || audit_id::text);
            INSERT INTO public.patient_debt_log(
                patient_record_id, reference_code, user_id, "timestamp", debt_change, debt_direction)
            VALUES ((row_json->>'record_id')::integer, debt_reference, audit_user, audit_time,
                    ABS(debt_delta), CASE WHEN debt_delta > 0 THEN '+' ELSE '-' END);
        END IF;
    END IF;
    RETURN COALESCE(NEW, OLD);
END $_$;


--
-- Name: pharmacy_apply_consignment_return(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_apply_consignment_return() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    movement_code varchar;
    active_barcode varchar;
BEGIN
    SELECT code INTO STRICT movement_code
    FROM public."qrMovementTypes"
    WHERE record_id = NEW.movement_type;

    IF movement_code <> 'CONSIGN_RETURN' THEN
        RETURN NEW;
    END IF;

    SELECT barcode INTO active_barcode
    FROM public."qrActive"
    WHERE qr_code = NEW.qr_code
      AND UPPER(COALESCE(status, '')) IN ('RESERVED', 'EMANET')
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Emanet karekod aktif ve ayrılmış durumda bulunamadı: %', NEW.qr_code
            USING ERRCODE = 'P0002';
    END IF;

    UPDATE public."qrActive"
    SET status = NULL,
        reference_no = NEW.reference_code
    WHERE qr_code = NEW.qr_code;

    PERFORM public.pharmacy_refresh_consignment_quantity(active_barcode);
    RETURN NEW;
END;
$$;


--
-- Name: pharmacy_apply_qr_movement(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_apply_qr_movement() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
 IF NEW.movement_date > (clock_timestamp() AT TIME ZONE 'Europe/Istanbul') THEN
  INSERT INTO public.scheduled_stock_movements(record_id) VALUES(NEW.record_id) ON CONFLICT DO NOTHING;
 ELSE
  PERFORM public.pharmacy_apply_qr_movement_record(NEW);
 END IF;
 RETURN NEW;
END $$;


SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: qrMovements; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."qrMovements" (
    record_id integer NOT NULL,
    qr_code character varying NOT NULL,
    movement_date timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    movement_address character varying,
    user_id character varying,
    reference_code character varying,
    movement_type smallint NOT NULL,
    notes text DEFAULT ''::text NOT NULL
);

ALTER TABLE ONLY public."qrMovements" FORCE ROW LEVEL SECURITY;


--
-- Name: pharmacy_apply_qr_movement_record(public."qrMovements"); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_apply_qr_movement_record(movement public."qrMovements") RETURNS void
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
    takas_item_id integer;
    takas_item_debt numeric(12,2);
BEGIN
    SELECT code INTO STRICT movement_code
    FROM public."qrMovementTypes"
    WHERE record_id = movement.movement_type;

    SELECT b.barcode INTO barcode_only
    FROM public.barcode b
    WHERE b.barcode = movement.qr_code
    LIMIT 1;

    IF barcode_only IS NOT NULL THEN
        IF movement_code = 'TRANSF_OUT' THEN
            RAISE EXCEPTION 'Takas çıkışı tekil ilaç karekodu gerektirir: %', barcode_only
                USING ERRCODE = '22023';
        ELSIF movement_code IN (
            'DEP_IN', 'FIRM_IN', 'TRANSF_IN', 'PERAK_RETURN',
            'PRESC_RETURN', 'MAN_IN', 'QR_ADD'
        ) THEN
            INSERT INTO public."barcodeActive" (barcode, quantity)
            VALUES (barcode_only, 1)
            ON CONFLICT (barcode) DO UPDATE
            SET quantity = public."barcodeActive".quantity + 1;
        ELSIF movement_code IN (
            'DEP_RETURN', 'FIRM_RETURN', 'PERAK_OUT',
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
        RETURN;
    END IF;

    IF movement_code IN (
        'DEP_IN', 'FIRM_IN', 'TRANSF_IN', 'PERAK_RETURN',
        'PRESC_RETURN', 'MAN_IN', 'QR_ADD'
    ) THEN
        SELECT * INTO inactive_row
        FROM public."qrInactive"
        WHERE qr_code = movement.qr_code
        FOR UPDATE;

        parsed_barcode := COALESCE(inactive_row.barcode, public.pharmacy_qr_barcode(movement.qr_code));
        parsed_expiry := COALESCE(inactive_row.expiry_date, public.pharmacy_qr_expiry(movement.qr_code));

        IF parsed_barcode IS NULL THEN
            RAISE EXCEPTION 'Karekoddan barkod bilgisi okunamadı.'
                USING ERRCODE = '22023';
        END IF;

        INSERT INTO public."qrActive" (
            qr_code, barcode, expiry_date, status, purchase_cost, reference_no
        ) VALUES (
            movement.qr_code, parsed_barcode, parsed_expiry, NULL,
            inactive_row.entry_cost, movement.reference_code
        )
        ON CONFLICT (qr_code) DO NOTHING;
        GET DIAGNOSTICS inserted_count = ROW_COUNT;

        IF inserted_count = 0 THEN
            RAISE EXCEPTION 'Karekod zaten aktif stokta: %', movement.qr_code
                USING ERRCODE = '23505';
        END IF;

        DELETE FROM public."qrInactive" WHERE qr_code = movement.qr_code;
        PERFORM public.pharmacy_refresh_barcode_active(parsed_barcode);
        RETURN;
    END IF;

    IF movement_code = 'TRANSF_OUT' THEN
        SELECT item.record_id, item.debt_amount
        INTO takas_item_id, takas_item_debt
        FROM public."takasQrItems" item
        JOIN public.takas transfer ON transfer.record_id = item.takas_id
        WHERE item.qr_code = movement.qr_code
          AND movement.reference_code = 'T' || transfer.record_id::text
          AND item.status = 'pending_transfer'
        FOR UPDATE OF item;

        IF takas_item_id IS NULL THEN
            RAISE EXCEPTION 'Takas hareketi için bekleyen karekod satırı bulunamadı: %', movement.qr_code
                USING ERRCODE = 'P0002';
        END IF;

        SELECT * INTO active_row
        FROM public."qrActive"
        WHERE qr_code = movement.qr_code
        FOR UPDATE;

        IF NOT FOUND THEN
            RAISE EXCEPTION 'Karekod aktif stokta bulunamadı: %', movement.qr_code
                USING ERRCODE = 'P0002';
        END IF;

        INSERT INTO public."qrHanged" (
            qr_code, takas_item_id, barcode, expiry_date,
            entry_cost, debt_amount, reference_no
        ) VALUES (
            active_row.qr_code, takas_item_id, active_row.barcode, active_row.expiry_date,
            active_row.purchase_cost, takas_item_debt, movement.reference_code
        );

        DELETE FROM public."qrActive" WHERE qr_code = movement.qr_code;
        PERFORM public.pharmacy_refresh_barcode_active(active_row.barcode);
        RETURN;
    END IF;

    IF movement_code IN (
        'DEP_RETURN', 'FIRM_RETURN', 'PERAK_OUT',
        'PRESC_OUT', 'DESTRUCTION', 'MAN_OUT'
    ) THEN
        SELECT * INTO active_row
        FROM public."qrActive"
        WHERE qr_code = movement.qr_code
        FOR UPDATE;

        IF NOT FOUND THEN
            RAISE EXCEPTION 'Karekod aktif stokta bulunamadı: %', movement.qr_code
                USING ERRCODE = 'P0002';
        END IF;

        INSERT INTO public."qrInactive" (
            qr_code, barcode, expiry_date, entry_cost, exit_price, reference_no
        ) VALUES (
            active_row.qr_code, active_row.barcode, active_row.expiry_date,
            active_row.purchase_cost, NULL, movement.reference_code
        )
        ON CONFLICT (qr_code) DO UPDATE
        SET barcode = EXCLUDED.barcode,
            expiry_date = EXCLUDED.expiry_date,
            entry_cost = EXCLUDED.entry_cost,
            exit_price = EXCLUDED.exit_price,
            reference_no = EXCLUDED.reference_no;

        DELETE FROM public."qrActive" WHERE qr_code = movement.qr_code;
        PERFORM public.pharmacy_refresh_barcode_active(active_row.barcode);
        RETURN;
    END IF;

    SELECT * INTO active_row
    FROM public."qrActive"
    WHERE qr_code = movement.qr_code
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Karekod aktif stokta bulunamadı: %', movement.qr_code
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
    reference_no = COALESCE(movement.reference_code, reference_no)
    WHERE qr_code = movement.qr_code;

    RETURN;
END;
$$;


--
-- Name: pharmacy_apply_refund_payment_status(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_apply_refund_payment_status() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.refund_id IS DISTINCT FROM NEW.refund_id THEN
        PERFORM public.pharmacy_refresh_refund_payment_status(OLD.refund_id);
    END IF;
    PERFORM public.pharmacy_refresh_refund_payment_status(
        CASE WHEN TG_OP = 'DELETE' THEN OLD.refund_id ELSE NEW.refund_id END);
    RETURN COALESCE(NEW, OLD);
END;
$$;


--
-- Name: pharmacy_apply_sale_payment_status(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_apply_sale_payment_status() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.sale_id IS DISTINCT FROM NEW.sale_id THEN
        PERFORM public.pharmacy_refresh_sale_payment_status(OLD.sale_id);
    END IF;
    PERFORM public.pharmacy_refresh_sale_payment_status(
        CASE WHEN TG_OP = 'DELETE' THEN OLD.sale_id ELSE NEW.sale_id END);
    RETURN COALESCE(NEW, OLD);
END;
$$;


--
-- Name: pharmacy_qr_barcode(character varying); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_qr_barcode(value character varying) RETURNS character varying
    LANGUAGE plpgsql STABLE STRICT
    AS $$
DECLARE
    gtin varchar := public.pharmacy_qr_gtin14(value);
    gtin13 varchar;
    catalog_barcode varchar;
BEGIN
    IF gtin IS NULL THEN
        RETURN NULL;
    END IF;

    gtin13 := CASE WHEN left(gtin, 1) = '0' THEN substring(gtin FROM 2) ELSE gtin END;
    SELECT b.barcode
    INTO catalog_barcode
    FROM public.barcode b
    WHERE b.barcode IN (gtin, gtin13)
    ORDER BY CASE WHEN b.barcode = gtin13 THEN 0 ELSE 1 END
    LIMIT 1;

    RETURN COALESCE(catalog_barcode, gtin13);
END;
$$;


--
-- Name: pharmacy_qr_expiry(character varying); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_qr_expiry(value character varying) RETURNS date
    LANGUAGE plpgsql IMMUTABLE STRICT
    AS $$
DECLARE
    cleaned varchar := regexp_replace(value, E'^\\]d2', '');
    encoded varchar;
    parsed_year integer;
    parsed_month integer;
    parsed_day integer;
BEGIN
    encoded := substring(cleaned FROM E'\\(17\\)([0-9]{6})');
    IF encoded IS NULL AND cleaned ~ '^01[0-9]{14}' THEN
        encoded := substring(substring(cleaned FROM 17) FROM '17([0-9]{6})');
    END IF;
    IF encoded IS NULL THEN
        RETURN NULL;
    END IF;

    parsed_year := 2000 + substring(encoded FROM 1 FOR 2)::integer;
    parsed_month := substring(encoded FROM 3 FOR 2)::integer;
    parsed_day := substring(encoded FROM 5 FOR 2)::integer;
    IF parsed_month NOT BETWEEN 1 AND 12 THEN
        RETURN NULL;
    END IF;
    IF parsed_day = 0 THEN
        RETURN (make_date(parsed_year, parsed_month, 1) + interval '1 month - 1 day')::date;
    END IF;
    RETURN make_date(parsed_year, parsed_month, parsed_day);
EXCEPTION WHEN datetime_field_overflow THEN
    RETURN NULL;
END;
$$;


--
-- Name: pharmacy_qr_gtin14(character varying); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_qr_gtin14(value character varying) RETURNS character varying
    LANGUAGE plpgsql IMMUTABLE STRICT
    AS $$
DECLARE
    cleaned varchar := regexp_replace(value, E'^\\]d2', '');
    parsed varchar;
BEGIN
    parsed := substring(cleaned FROM E'\\(01\\)([0-9]{14})');
    IF parsed IS NULL AND cleaned ~ '^01[0-9]{14}' THEN
        parsed := substring(cleaned FROM 3 FOR 14);
    END IF;
    RETURN parsed;
END;
$$;


--
-- Name: pharmacy_refresh_barcode_active(character varying); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_refresh_barcode_active(target_barcode character varying) RETURNS void
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


--
-- Name: pharmacy_refresh_consignment_quantity(character varying); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_refresh_consignment_quantity(target_barcode character varying) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    UPDATE public."barcodeActive" aggregate_stock
    SET consignment_quantity = (
        SELECT COUNT(*)::integer FROM public."qrActive" active
        WHERE active.barcode = target_barcode
          AND UPPER(COALESCE(active.status, '')) IN ('RESERVED', 'EMANET')
    ) + (
        SELECT COUNT(*)::integer
        FROM public."consignmentSaleItems" item
        JOIN public."consignmentSales" sale ON sale.record_id=item.consignment_sale_id
        WHERE item.barcode=target_barcode AND sale.status='reserved'
          AND item.qr_code LIKE 'FARMASSIST-BARCODE:%'
    )
    WHERE aggregate_stock.barcode = target_barcode;
END;
$$;


--
-- Name: pharmacy_refresh_refund_payment_status(integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_refresh_refund_payment_status(target_refund_id integer) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    refund_due numeric(12,2);
    paid_total numeric(12,2);
BEGIN
    SELECT CASE
            WHEN refund_source='prescription' THEN patient_amount
            ELSE GREATEST(total_amount - debt_reduction_amount, 0)
        END
    INTO refund_due
    FROM public."salesRefunds"
    WHERE record_id = target_refund_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN;
    END IF;

    SELECT COALESCE(SUM(amount) FILTER (WHERE payment_finished), 0)
    INTO paid_total
    FROM public."salesRefundPayements"
    WHERE refund_id = target_refund_id;

    UPDATE public."salesRefunds"
    SET payement_finished = paid_total = refund_due
    WHERE record_id = target_refund_id;
END;
$$;


--
-- Name: pharmacy_refresh_sale_payment_status(integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.pharmacy_refresh_sale_payment_status(target_sale_id integer) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    sale_total numeric(12,2);
    institution_due numeric(12,2);
    customer_due numeric(12,2);
    customer_paid numeric(12,2);
    institution_paid numeric(12,2);
BEGIN
    SELECT total_amount, institution_amount
    INTO sale_total, institution_due
    FROM public.sales
    WHERE record_id = target_sale_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN;
    END IF;

    customer_due := GREATEST(sale_total - institution_due, 0);

    SELECT
        COALESCE(SUM(payment.amount) FILTER (
            WHERE type.payment_type NOT IN ('institution', 'institution_secondary')
              AND payment.payment_finished), 0),
        COALESCE(SUM(payment.amount) FILTER (
            WHERE type.payment_type IN ('institution', 'institution_secondary')
              AND payment.payment_finished), 0)
    INTO customer_paid, institution_paid
    FROM public."salesPayements" payment
    JOIN public."payementTypes" type ON type.record_id = payment.payment_type
    WHERE payment.sale_id = target_sale_id;

    UPDATE public.sales
    SET payement_finished = customer_paid >= customer_due,
        institution_payement_finished = institution_paid >= institution_due
    WHERE record_id = target_sale_id;
END;
$$;


--
-- Name: refresh_family_group_members(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.refresh_family_group_members() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE old_group integer; new_group integer; refreshed_members integer[];
BEGIN
    old_group := CASE WHEN TG_OP='INSERT' THEN NULL ELSE OLD.family_group_id END;
    new_group := CASE WHEN TG_OP='DELETE' THEN NULL ELSE NEW.family_group_id END;
    IF old_group IS NOT NULL THEN
        SELECT COALESCE(array_agg(patient.record_id ORDER BY patient.record_id), ARRAY[]::integer[])
        INTO refreshed_members
        FROM public.patient patient WHERE patient.family_group_id=old_group;
        UPDATE public.family_group group_row
        SET member_ids=refreshed_members,
            main_patient_id=CASE
                WHEN group_row.main_patient_id=ANY(refreshed_members) THEN group_row.main_patient_id
                ELSE refreshed_members[1]
            END
        WHERE group_row.record_id=old_group;
    END IF;
    IF new_group IS NOT NULL AND new_group IS DISTINCT FROM old_group THEN
        UPDATE public.family_group group_row
        SET member_ids=COALESCE((SELECT array_agg(patient.record_id ORDER BY patient.record_id)
                                 FROM public.patient patient WHERE patient.family_group_id=new_group), ARRAY[]::integer[])
        WHERE group_row.record_id=new_group;
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;


--
-- Name: refresh_patient_prescription_count_for_identity(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.refresh_patient_prescription_count_for_identity() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    patient_id integer;
    refreshed_count integer;
BEGIN
    FOREACH patient_id IN ARRAY ARRAY[
        CASE WHEN TG_OP='INSERT' THEN NULL ELSE OLD.patient_record_id END,
        CASE WHEN TG_OP='DELETE' THEN NULL ELSE NEW.patient_record_id END
    ]
    LOOP
        IF patient_id IS NOT NULL THEN
            SELECT COUNT(*)::integer
              INTO refreshed_count
              FROM public.patient_identity identity_row
              JOIN public.prescription prescription
                ON prescription.patient_tc_identity_no=identity_row.tc_identity_no
             WHERE identity_row.patient_record_id=patient_id
               AND NOT COALESCE(prescription.is_deleted, false);
            UPDATE public.patient patient
               SET prescription_count=refreshed_count
             WHERE patient.record_id=patient_id
               AND patient.prescription_count IS DISTINCT FROM refreshed_count;
        END IF;

        EXIT WHEN TG_OP='UPDATE' AND OLD.patient_record_id=NEW.patient_record_id;
    END LOOP;
    RETURN COALESCE(NEW, OLD);
END $$;


--
-- Name: refresh_visible_activity(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.refresh_visible_activity() RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
DECLARE movement public."qrMovements"%ROWTYPE;
BEGIN
 PERFORM pg_advisory_xact_lock(hashtext('refresh_visible_activity'));
 FOR movement IN SELECT q.* FROM public.scheduled_stock_movements pending JOIN public."qrMovements" q USING(record_id)
   WHERE q.movement_date <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul') ORDER BY q.movement_date,q.record_id
 LOOP
  PERFORM public.pharmacy_apply_qr_movement_record(movement);
  UPDATE public."qrActive" q SET purchase_cost=cost.unit_price FROM (
    SELECT line.unit_price FROM public.invoice_lines line JOIN public.invoice_line_qr_codes code ON code.invoice_line_id=line.record_id
    JOIN public.invoices i USING(invoice_uuid) WHERE code.qr_code=movement.qr_code AND i.database_entry_at<=clock_timestamp()
    ORDER BY i.database_entry_at DESC LIMIT 1) cost WHERE q.qr_code=movement.qr_code AND q.purchase_cost IS NULL;
  UPDATE public."qrInactive" q SET exit_price=price.exit_price FROM (
    SELECT item.exit_price FROM public."salesQrItems" item JOIN public.sales s ON s.record_id=item.sale_id
    WHERE item.qr_code=movement.qr_code AND s.timestamp<=(clock_timestamp() AT TIME ZONE 'Europe/Istanbul')
    ORDER BY s.timestamp DESC LIMIT 1) price WHERE q.qr_code=movement.qr_code;
  DELETE FROM public.scheduled_stock_movements WHERE record_id=movement.record_id;
 END LOOP;
 UPDATE public.patient p SET prescription_count=counted.total FROM (
   SELECT i.patient_record_id,count(*)::integer total FROM public.patient_identity i JOIN public.prescription r ON r.patient_tc_identity_no=i.tc_identity_no
   WHERE NOT r.is_deleted AND r.saved_at<=clock_timestamp() AND r.prescription_date<=(clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date GROUP BY i.patient_record_id
 ) counted WHERE p.record_id=counted.patient_record_id AND p.prescription_count IS DISTINCT FROM counted.total;
 UPDATE public.depot d SET debt_balance=counted.total FROM (
   SELECT d2.record_id,coalesce(sum(i.payable_amount),0) total FROM public.depot d2 LEFT JOIN public.addresses a ON a.gln=d2.gln_code
   LEFT JOIN public.invoices i ON i.sender_address_id=a.record_id AND i.database_entry_at<=clock_timestamp() AND i.issue_date<=(clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date GROUP BY d2.record_id
 ) counted WHERE d.record_id=counted.record_id AND d.debt_balance IS DISTINCT FROM counted.total;
 UPDATE public.pharmacy p SET debt_balance=counted.total FROM (
   SELECT p2.record_id,coalesce((SELECT sum(q.debt_amount) FROM public."takasQrItems" q JOIN public.takas t ON t.record_id=q.takas_id
     WHERE t.pharmacy_id=p2.record_id AND q.status='accepted' AND t.created_at<=(clock_timestamp() AT TIME ZONE 'Europe/Istanbul')),0)
   +coalesce((SELECT sum(q.debt_amount) FROM public."takasBarcodeItems" q JOIN public.takas t ON t.record_id=q.takas_id
     WHERE t.pharmacy_id=p2.record_id AND q.status='accepted' AND t.created_at<=(clock_timestamp() AT TIME ZONE 'Europe/Istanbul')),0) total
   FROM public.pharmacy p2
 ) counted WHERE p.record_id=counted.record_id AND p.debt_balance IS DISTINCT FROM counted.total;
END $$;


--
-- Name: track_patient_prescription_count(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.track_patient_prescription_count() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    old_is_counted boolean := TG_OP <> 'INSERT'
        AND OLD.patient_tc_identity_no IS NOT NULL
        AND NOT COALESCE(OLD.is_deleted, false) AND OLD.saved_at <= clock_timestamp() AND OLD.prescription_date <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date;
    new_is_counted boolean := TG_OP <> 'DELETE'
        AND NEW.patient_tc_identity_no IS NOT NULL
        AND NOT COALESCE(NEW.is_deleted, false) AND NEW.saved_at <= clock_timestamp() AND NEW.prescription_date <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date;
BEGIN
    IF old_is_counted AND (NOT new_is_counted OR OLD.patient_tc_identity_no IS DISTINCT FROM NEW.patient_tc_identity_no) THEN
        PERFORM public.adjust_patient_prescription_count(OLD.patient_tc_identity_no, -1);
    END IF;
    IF new_is_counted AND (NOT old_is_counted OR NEW.patient_tc_identity_no IS DISTINCT FROM OLD.patient_tc_identity_no) THEN
        PERFORM public.adjust_patient_prescription_count(NEW.patient_tc_identity_no, 1);
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;


--
-- Name: validate_family_group_members(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.validate_family_group_members() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE missing_id integer;
BEGIN
    NEW.family_group_name := NULLIF(BTRIM(NEW.family_group_name), '');
    IF NEW.family_group_name IS NULL THEN
        RAISE EXCEPTION 'Family group name cannot be empty';
    END IF;
    NEW.member_ids := ARRAY(SELECT DISTINCT value FROM unnest(COALESCE(NEW.member_ids, ARRAY[]::integer[])) value ORDER BY value);
    IF NEW.main_patient_id IS NOT NULL AND NOT (NEW.main_patient_id = ANY(NEW.member_ids)) THEN
        NEW.member_ids := array_prepend(NEW.main_patient_id, NEW.member_ids);
    END IF;
    SELECT value INTO missing_id
    FROM unnest(NEW.member_ids) value
    LEFT JOIN public.patient patient ON patient.record_id=value
    WHERE patient.record_id IS NULL LIMIT 1;
    IF missing_id IS NOT NULL THEN
        RAISE EXCEPTION 'Family group member patient % does not exist', missing_id;
    END IF;
    RETURN NEW;
END $$;


--
-- Name: validate_prescription_icd10_codes(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.validate_prescription_icd10_codes() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE unknown_code varchar;
BEGIN
    SELECT code INTO unknown_code
      FROM unnest(COALESCE(NEW.icd10_diagnoses, ARRAY[]::varchar[])) code
     WHERE NOT EXISTS (SELECT 1 FROM public.icd10_diagnosis ref WHERE ref.code=code)
     LIMIT 1;
    IF unknown_code IS NOT NULL THEN
        RAISE EXCEPTION 'Unknown ICD-10 code: %', unknown_code USING ERRCODE='23503';
    END IF;
    RETURN NEW;
END $$;


--
-- Name: addresses; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.addresses (
    record_id integer CONSTRAINT addresses_compact_record_id_not_null NOT NULL,
    party_name text DEFAULT ''::text CONSTRAINT addresses_compact_party_name_not_null NOT NULL,
    identity_no text DEFAULT ''::text CONSTRAINT addresses_compact_identity_no_not_null NOT NULL,
    gln text DEFAULT ''::text NOT NULL
);


--
-- Name: COLUMN addresses.gln; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.addresses.gln IS '13-digit Global Location Number; an address may be identified by this value, identity_no, or both.';


--
-- Name: addresses_compact_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.addresses ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.addresses_compact_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: barcode; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.barcode (
    record_id integer CONSTRAINT barcode_record_id_not_null1 NOT NULL,
    medicine_name character varying,
    photo bytea[],
    barcode character varying,
    product_type_name character varying,
    company_name character varying,
    origin character varying,
    equivalent_group_code character varying,
    equivalent_classification character varying,
    active_ingredient character varying,
    atc_code character varying,
    sgk_code character varying,
    indication_summary text,
    dosage_instructions text,
    market_status integer,
    source_product_id integer,
    brand_name character varying,
    package_description character varying,
    pharmaceutical_form character varying,
    prescription_type_id integer,
    license_number character varying,
    license_date character varying,
    shelf_life character varying,
    generic_code character varying,
    nfc_code character varying,
    source_company_id integer,
    pregnancy_category character varying,
    old_name text,
    free_text text,
    warnings jsonb,
    sut_rules jsonb,
    indications jsonb,
    product_type_id integer,
    product_form_code character varying,
    prescription_type_name character varying,
    generic_name character varying,
    reimbursement_status_code integer,
    reimbursement_status_description text,
    sut_summary_code character varying,
    reference_price_group character varying,
    is_ek4f boolean,
    twenty_year_status boolean,
    kamu_no character varying,
    strength character varying,
    product_prefix character varying,
    all_equivalents character varying[],
    sut_summary jsonb,
    medula_details jsonb,
    medula_info jsonb
);


--
-- Name: barcodeActive; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."barcodeActive" (
    barcode character varying NOT NULL,
    quantity integer DEFAULT 0 NOT NULL,
    nearest_expiry_date date,
    status character varying,
    order_quantity integer DEFAULT 0 NOT NULL,
    consignment_quantity integer DEFAULT 0 NOT NULL,
    pending_quantity integer DEFAULT 0 NOT NULL,
    order_reference_codes character varying[] DEFAULT ARRAY[]::character varying[] NOT NULL,
    CONSTRAINT "barcodeActive_consignment_quantity_check" CHECK ((consignment_quantity >= 0)),
    CONSTRAINT "barcodeActive_order_quantity_check" CHECK ((order_quantity >= 0)),
    CONSTRAINT "barcodeActive_pending_quantity_check" CHECK ((pending_quantity >= 0)),
    CONSTRAINT "barcodeActive_quantity_check" CHECK ((quantity >= 0))
);


--
-- Name: barcodeAnalysis; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."barcodeAnalysis" (
    record_id integer NOT NULL,
    record_reference_no integer,
    wanted_stock_months numeric(4,1) DEFAULT 1.5 NOT NULL,
    updated_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    CONSTRAINT ck_barcode_analysis_wanted_stock_months CHECK (((wanted_stock_months >= 0.5) AND (wanted_stock_months <= 60.0) AND ((wanted_stock_months * (2)::numeric) = trunc((wanted_stock_months * (2)::numeric)))))
);

ALTER TABLE ONLY public."barcodeAnalysis" FORCE ROW LEVEL SECURITY;


--
-- Name: barcodeAnalysis_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."barcodeAnalysis" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."barcodeAnalysis_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: barcodeCampaign; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."barcodeCampaign" (
    campaign_id bigint CONSTRAINT "barcodeCampaign_campaign_id_not_null1" NOT NULL,
    record_reference_no bigint CONSTRAINT "barcodeCampaign_record_reference_no_not_null1" NOT NULL,
    warehouse_gln character varying CONSTRAINT "barcodeCampaign_warehouse_gln_not_null1" NOT NULL,
    purchase_quantity integer CONSTRAINT "barcodeCampaign_purchase_quantity_not_null1" NOT NULL,
    bonus_quantity integer CONSTRAINT "barcodeCampaign_bonus_quantity_not_null1" NOT NULL,
    period_days integer,
    start_date date,
    end_date date,
    CONSTRAINT barcode_campaign_bonus_quantity_check CHECK ((bonus_quantity >= 0)),
    CONSTRAINT barcode_campaign_dates_check CHECK (((start_date IS NULL) OR (end_date IS NULL) OR (end_date >= start_date))),
    CONSTRAINT barcode_campaign_period_days_check CHECK (((period_days IS NULL) OR (period_days > 0))),
    CONSTRAINT barcode_campaign_purchase_quantity_check CHECK ((purchase_quantity > 0))
);

ALTER TABLE ONLY public."barcodeCampaign" FORCE ROW LEVEL SECURITY;


--
-- Name: barcodeCampaign_campaign_id_seq1; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."barcodeCampaign" ALTER COLUMN campaign_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."barcodeCampaign_campaign_id_seq1"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: barcodePriceHistory; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."barcodePriceHistory" (
    history_id bigint CONSTRAINT "barcodePriceHistory_history_id_not_null1" NOT NULL,
    record_reference_no bigint CONSTRAINT "barcodePriceHistory_record_reference_no_not_null1" NOT NULL,
    effective_date date CONSTRAINT "barcodePriceHistory_effective_date_not_null1" NOT NULL,
    sale_price numeric(12,2),
    public_price numeric(12,2),
    public_paid_price numeric(12,2),
    price_difference numeric(12,2),
    vat_rate numeric(5,2),
    discount_rate numeric(5,2),
    warehouse_price numeric(12,2),
    manufacturer_price numeric(12,2)
);

ALTER TABLE ONLY public."barcodePriceHistory" FORCE ROW LEVEL SECURITY;


--
-- Name: barcodePriceHistory_history_id_seq1; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."barcodePriceHistory" ALTER COLUMN history_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."barcodePriceHistory_history_id_seq1"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: barcodePrices; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."barcodePrices" (
    record_reference_no integer CONSTRAINT "barcodePrices_record_reference_no_not_null1" NOT NULL,
    purchase_price numeric,
    sale_price numeric,
    public_price numeric,
    vat_rate numeric,
    discount_rate numeric,
    warehouse_price numeric,
    manufacturer_price numeric,
    currency character varying,
    price_effective_date character varying,
    price_type character varying,
    public_paid_price numeric,
    price_difference numeric
);


--
-- Name: barcode_record_id_seq1; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.barcode ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.barcode_record_id_seq1
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: branches; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.branches (
    branch_id integer NOT NULL,
    branch_name character varying NOT NULL,
    parent_branch_id integer,
    display_order integer NOT NULL
);


--
-- Name: consignmentSaleItems; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."consignmentSaleItems" (
    record_id integer NOT NULL,
    consignment_sale_id integer NOT NULL,
    qr_code character varying NOT NULL,
    barcode character varying NOT NULL,
    gross_price numeric(12,2) NOT NULL,
    discount_amount numeric(12,2) DEFAULT 0 NOT NULL,
    net_price numeric(12,2) NOT NULL,
    entry_cost numeric(12,2),
    CONSTRAINT ck_consignment_sale_items_net_price CHECK ((net_price = (gross_price - discount_amount))),
    CONSTRAINT "consignmentSaleItems_check" CHECK (((discount_amount >= (0)::numeric) AND (discount_amount <= gross_price))),
    CONSTRAINT "consignmentSaleItems_gross_price_check" CHECK ((gross_price >= (0)::numeric)),
    CONSTRAINT "consignmentSaleItems_net_price_check" CHECK ((net_price >= (0)::numeric))
);

ALTER TABLE ONLY public."consignmentSaleItems" FORCE ROW LEVEL SECURITY;


--
-- Name: consignmentSaleItems_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."consignmentSaleItems" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."consignmentSaleItems_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: consignmentSalePayements; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."consignmentSalePayements" (
    record_id integer NOT NULL,
    consignment_sale_id integer NOT NULL,
    payment_type smallint NOT NULL,
    amount numeric(12,2) NOT NULL,
    settlement_status character varying DEFAULT 'reserved'::character varying NOT NULL,
    settled_at timestamp without time zone,
    CONSTRAINT ck_consignment_sale_payement_status CHECK (((settlement_status)::text = ANY (ARRAY[('reserved'::character varying)::text, ('applied_to_retail'::character varying)::text, ('refunded'::character varying)::text, ('debt_reversed'::character varying)::text, ('closed'::character varying)::text]))),
    CONSTRAINT "consignmentSalePayements_amount_check" CHECK ((amount > (0)::numeric))
);

ALTER TABLE ONLY public."consignmentSalePayements" FORCE ROW LEVEL SECURITY;


--
-- Name: consignmentSalePayements_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."consignmentSalePayements" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."consignmentSalePayements_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: consignmentSales; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."consignmentSales" (
    record_id integer NOT NULL,
    patient_id integer NOT NULL,
    total_amount numeric(12,2) NOT NULL,
    paid_amount numeric(12,2) DEFAULT 0 NOT NULL,
    debt_amount numeric(12,2) DEFAULT 0 NOT NULL,
    discount_amount numeric(12,2) DEFAULT 0 NOT NULL,
    refunded_amount numeric(12,2) DEFAULT 0 NOT NULL,
    debt_reversed_amount numeric(12,2) DEFAULT 0 NOT NULL,
    with_iskonto boolean DEFAULT false NOT NULL,
    status character varying DEFAULT 'reserved'::character varying NOT NULL,
    settlement_sale_id integer,
    user_no character varying,
    settled_by character varying,
    "timestamp" timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    settled_at timestamp without time zone,
    marked boolean DEFAULT false NOT NULL,
    notes character varying,
    CONSTRAINT ck_consignment_sales_accounting CHECK ((((paid_amount + debt_amount) + discount_amount) = total_amount)),
    CONSTRAINT ck_consignment_sales_status CHECK (((status)::text = ANY (ARRAY[('reserved'::character varying)::text, ('converted_to_retail'::character varying)::text, ('returned'::character varying)::text, ('converted_to_prescription'::character varying)::text]))),
    CONSTRAINT "consignmentSales_debt_amount_check" CHECK ((debt_amount >= (0)::numeric)),
    CONSTRAINT "consignmentSales_debt_reversed_amount_check" CHECK ((debt_reversed_amount >= (0)::numeric)),
    CONSTRAINT "consignmentSales_discount_amount_check" CHECK ((discount_amount >= (0)::numeric)),
    CONSTRAINT "consignmentSales_paid_amount_check" CHECK ((paid_amount >= (0)::numeric)),
    CONSTRAINT "consignmentSales_refunded_amount_check" CHECK ((refunded_amount >= (0)::numeric)),
    CONSTRAINT "consignmentSales_total_amount_check" CHECK ((total_amount >= (0)::numeric))
);

ALTER TABLE ONLY public."consignmentSales" FORCE ROW LEVEL SECURITY;


--
-- Name: consignmentSales_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."consignmentSales" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."consignmentSales_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: depot; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.depot (
    gln_code character varying,
    depot_name character varying,
    contact_person character varying,
    e_invoice_username character varying,
    e_invoice_password character varying,
    e_invoice_region_name character varying,
    address text,
    city character varying,
    district character varying,
    postal_code character varying,
    business_phone_numbers character varying[],
    fax_numbers character varying[],
    email_addresses character varying[],
    website character varying,
    tax_no character varying,
    tax_office character varying,
    is_active boolean,
    uts_company_numbers character varying[],
    web_username character varying,
    web_password character varying,
    record_id integer NOT NULL,
    debt_balance numeric,
    medicines_in_transit character varying[]
);


--
-- Name: depot_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.depot_log (
    log_id bigint NOT NULL,
    record_key character varying NOT NULL,
    operation character varying(6) NOT NULL,
    old_data jsonb,
    new_data jsonb,
    user_id character varying NOT NULL,
    "timestamp" timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    CONSTRAINT depot_log_operation_check CHECK (((operation)::text = ANY (ARRAY[('INSERT'::character varying)::text, ('UPDATE'::character varying)::text, ('DELETE'::character varying)::text])))
);

ALTER TABLE ONLY public.depot_log FORCE ROW LEVEL SECURITY;


--
-- Name: depot_log_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.depot_log ALTER COLUMN log_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.depot_log_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: depot_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.depot ALTER COLUMN record_id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.depot_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: doctor; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.doctor (
    diploma_registration_no character varying,
    diploma_no character varying,
    first_name character varying,
    last_name character varying,
    certificate character varying,
    facility_no character varying,
    tc_identity_no character varying,
    record_id integer NOT NULL,
    phone_numbers character varying[],
    email_addresses character varying[],
    branch_id integer
);


--
-- Name: doctor_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.doctor_log (
    log_id bigint NOT NULL,
    record_key character varying NOT NULL,
    operation character varying(6) NOT NULL,
    old_data jsonb,
    new_data jsonb,
    user_id character varying NOT NULL,
    "timestamp" timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    CONSTRAINT doctor_log_operation_check CHECK (((operation)::text = ANY (ARRAY[('INSERT'::character varying)::text, ('UPDATE'::character varying)::text, ('DELETE'::character varying)::text])))
);

ALTER TABLE ONLY public.doctor_log FORCE ROW LEVEL SECURITY;


--
-- Name: doctor_log_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.doctor_log ALTER COLUMN log_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.doctor_log_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: doctor_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.doctor ALTER COLUMN record_id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.doctor_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: documents; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.documents (
    barcode character varying NOT NULL,
    kub bytea,
    kit bytea
);


--
-- Name: faculty; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.faculty (
    faculty_code character varying,
    faculty_name character varying,
    address text,
    city character varying,
    district character varying,
    country character varying,
    ownership_type character varying,
    faculty_type_id integer,
    tax_no character varying,
    tax_office character varying,
    phone_numbers character varying[],
    record_id integer NOT NULL,
    email_addresses character varying[]
);


--
-- Name: faculty_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.faculty_log (
    log_id bigint NOT NULL,
    record_key character varying NOT NULL,
    operation character varying(6) NOT NULL,
    old_data jsonb,
    new_data jsonb,
    user_id character varying NOT NULL,
    "timestamp" timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    CONSTRAINT faculty_log_operation_check CHECK (((operation)::text = ANY (ARRAY[('INSERT'::character varying)::text, ('UPDATE'::character varying)::text, ('DELETE'::character varying)::text])))
);

ALTER TABLE ONLY public.faculty_log FORCE ROW LEVEL SECURITY;


--
-- Name: faculty_log_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.faculty_log ALTER COLUMN log_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.faculty_log_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: faculty_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.faculty ALTER COLUMN record_id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.faculty_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: faculty_type; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.faculty_type (
    faculty_type_id integer NOT NULL,
    faculty_type_name character varying NOT NULL
);


--
-- Name: family_group; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.family_group (
    record_id integer NOT NULL,
    family_group_name character varying NOT NULL,
    main_patient_id integer,
    member_ids integer[] DEFAULT ARRAY[]::integer[] NOT NULL
);


--
-- Name: family_group_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.family_group_log (
    log_id bigint NOT NULL,
    record_key character varying NOT NULL,
    operation character varying(6) NOT NULL,
    old_data jsonb,
    new_data jsonb,
    user_id character varying NOT NULL,
    "timestamp" timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    CONSTRAINT family_group_log_operation_check CHECK (((operation)::text = ANY (ARRAY[('INSERT'::character varying)::text, ('UPDATE'::character varying)::text, ('DELETE'::character varying)::text])))
);

ALTER TABLE ONLY public.family_group_log FORCE ROW LEVEL SECURITY;


--
-- Name: family_group_log_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.family_group_log ALTER COLUMN log_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.family_group_log_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: family_group_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.family_group ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.family_group_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: icd10_diagnosis; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.icd10_diagnosis (
    code character varying NOT NULL,
    description character varying NOT NULL
);


--
-- Name: invoice_incremental_sync_items; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.invoice_incremental_sync_items (
    run_id uuid NOT NULL,
    type_key text NOT NULL,
    document_uuid text NOT NULL,
    document_number text DEFAULT ''::text NOT NULL,
    issue_date date,
    received_at timestamp with time zone,
    status text DEFAULT 'Pending'::text NOT NULL,
    last_error text DEFAULT ''::text NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT invoice_incremental_sync_item_status_check CHECK ((status = ANY (ARRAY['Pending'::text, 'Imported'::text, 'Failed'::text])))
);

ALTER TABLE ONLY public.invoice_incremental_sync_items FORCE ROW LEVEL SECURITY;


--
-- Name: TABLE invoice_incremental_sync_items; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.invoice_incremental_sync_items IS 'Per-invoice checkpoint rows for resumable Eczaci Kart incremental XML imports.';


--
-- Name: invoice_incremental_sync_state; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.invoice_incremental_sync_state (
    source_key text NOT NULL,
    last_successful_cutoff timestamp with time zone,
    active_run_id uuid,
    active_run_cutoff timestamp with time zone,
    phase text DEFAULT 'Idle'::text NOT NULL,
    total integer DEFAULT 0 NOT NULL,
    completed integer DEFAULT 0 NOT NULL,
    imported integer DEFAULT 0 NOT NULL,
    failed integer DEFAULT 0 NOT NULL,
    current_invoice text DEFAULT ''::text NOT NULL,
    last_error text DEFAULT ''::text NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT invoice_incremental_sync_phase_check CHECK ((phase = ANY (ARRAY['Idle'::text, 'Discovering'::text, 'Running'::text, 'Paused'::text, 'Faulted'::text, 'Completed'::text])))
);

ALTER TABLE ONLY public.invoice_incremental_sync_state FORCE ROW LEVEL SECURITY;


--
-- Name: TABLE invoice_incremental_sync_state; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.invoice_incremental_sync_state IS 'Durable Eczaci Kart incremental XML import watermark and active-run summary.';


--
-- Name: invoice_line_qr_codes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.invoice_line_qr_codes (
    record_id bigint NOT NULL,
    invoice_line_id integer NOT NULL,
    qr_code text NOT NULL,
    gtin text NOT NULL,
    barcode text NOT NULL,
    serial_number text NOT NULL,
    batch_number text NOT NULL,
    expiry_date date NOT NULL,
    raw_code text DEFAULT ''::text NOT NULL
);

ALTER TABLE ONLY public.invoice_line_qr_codes FORCE ROW LEVEL SECURITY;


--
-- Name: invoice_line_qr_codes_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.invoice_line_qr_codes ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.invoice_line_qr_codes_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: invoice_lines; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.invoice_lines (
    record_id integer CONSTRAINT invoice_lines_compact_record_id_not_null NOT NULL,
    invoice_uuid text CONSTRAINT invoice_lines_compact_invoice_uuid_not_null NOT NULL,
    line_number integer CONSTRAINT invoice_lines_compact_line_number_not_null NOT NULL,
    item_code text DEFAULT ''::text CONSTRAINT invoice_lines_compact_item_code_not_null NOT NULL,
    barcode text DEFAULT ''::text CONSTRAINT invoice_lines_compact_barcode_not_null NOT NULL,
    item_name text DEFAULT ''::text CONSTRAINT invoice_lines_compact_item_name_not_null NOT NULL,
    description text DEFAULT ''::text CONSTRAINT invoice_lines_compact_description_not_null NOT NULL,
    quantity numeric(18,4),
    unit text DEFAULT ''::text CONSTRAINT invoice_lines_compact_unit_not_null NOT NULL,
    unit_price numeric(18,4),
    vat_rate numeric(9,4),
    vat_amount numeric(18,4),
    discount_amount numeric(18,4),
    line_amount numeric(18,4),
    currency text DEFAULT ''::text CONSTRAINT invoice_lines_compact_currency_not_null NOT NULL,
    is_medication boolean CONSTRAINT invoice_lines_compact_is_medication_not_null NOT NULL,
    raw_data jsonb DEFAULT '{}'::jsonb CONSTRAINT invoice_lines_compact_raw_data_not_null NOT NULL,
    stock_tracking_type text DEFAULT 'NONE'::text NOT NULL,
    tracking_warning text DEFAULT ''::text NOT NULL,
    CONSTRAINT invoice_lines_stock_tracking_type_check CHECK ((stock_tracking_type = ANY (ARRAY['NONE'::text, 'BARCODE'::text, 'QR'::text])))
);

ALTER TABLE ONLY public.invoice_lines FORCE ROW LEVEL SECURITY;


--
-- Name: invoice_lines_compact_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.invoice_lines ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.invoice_lines_compact_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: invoice_stock_log_invoices; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.invoice_stock_log_invoices (
    invoice_stock_log_id bigint NOT NULL,
    invoice_uuid text NOT NULL
);

ALTER TABLE ONLY public.invoice_stock_log_invoices FORCE ROW LEVEL SECURITY;


--
-- Name: invoice_stock_logs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.invoice_stock_logs (
    record_id bigint NOT NULL,
    invoice_uuid text NOT NULL,
    action_code text NOT NULL,
    occurred_at timestamp with time zone DEFAULT now() NOT NULL,
    user_id character varying NOT NULL,
    reference_code character varying NOT NULL,
    related_log_id bigint,
    notes text DEFAULT ''::text NOT NULL,
    movement_type_id integer NOT NULL,
    CONSTRAINT invoice_stock_logs_action_check CHECK ((action_code = ANY (ARRAY['SAVE'::text, 'REFUND'::text, 'PARTIAL_REFUND'::text])))
);

ALTER TABLE ONLY public.invoice_stock_logs FORCE ROW LEVEL SECURITY;


--
-- Name: invoice_stock_logs_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.invoice_stock_logs ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.invoice_stock_logs_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: invoices; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.invoices (
    invoice_uuid text CONSTRAINT invoices_compact_invoice_uuid_not_null NOT NULL,
    invoice_type text CONSTRAINT invoices_compact_invoice_type_not_null NOT NULL,
    sender_address_id integer CONSTRAINT invoices_compact_sender_address_id_not_null NOT NULL,
    receiver_address_id integer CONSTRAINT invoices_compact_receiver_address_id_not_null NOT NULL,
    issue_date date,
    due_date date,
    database_entry_at timestamp with time zone DEFAULT now() CONSTRAINT invoices_compact_database_entry_at_not_null NOT NULL,
    total_cost numeric(18,4),
    discount_amount numeric(18,4),
    kdv_amount numeric(18,4),
    payable_amount numeric(18,4),
    is_saved boolean DEFAULT false NOT NULL,
    CONSTRAINT invoices_type_check CHECK ((invoice_type = ANY (ARRAY['Gelen Fatura'::text, 'Giden Fatura'::text, 'E-Arşiv Fatura'::text])))
);

ALTER TABLE ONLY public.invoices FORCE ROW LEVEL SECURITY;


--
-- Name: COLUMN invoices.is_saved; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.invoices.is_saved IS 'True when the invoice has been finalized into stock/movement history.';


--
-- Name: kurum; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.kurum (
    kurum_code character varying,
    kurum_name character varying,
    contact_person character varying,
    address text,
    city character varying,
    district character varying,
    phone_numbers character varying[],
    email_addresses character varying[],
    website character varying,
    gln_code character varying,
    tax_no character varying,
    tax_office character varying,
    record_id integer NOT NULL
);


--
-- Name: kurum_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.kurum_log (
    log_id bigint NOT NULL,
    record_key character varying NOT NULL,
    operation character varying(6) NOT NULL,
    old_data jsonb,
    new_data jsonb,
    user_id character varying NOT NULL,
    "timestamp" timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    CONSTRAINT kurum_log_operation_check CHECK (((operation)::text = ANY (ARRAY[('INSERT'::character varying)::text, ('UPDATE'::character varying)::text, ('DELETE'::character varying)::text])))
);

ALTER TABLE ONLY public.kurum_log FORCE ROW LEVEL SECURITY;


--
-- Name: kurum_log_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.kurum_log ALTER COLUMN log_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.kurum_log_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: kurum_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.kurum ALTER COLUMN record_id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.kurum_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: loyal_patient_benchmark_cohort; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.loyal_patient_benchmark_cohort (
    cohort_key character varying(64) NOT NULL,
    cohort_position integer NOT NULL,
    patient_record_id integer NOT NULL,
    selected_at timestamp with time zone DEFAULT now() NOT NULL
);

ALTER TABLE ONLY public.loyal_patient_benchmark_cohort FORCE ROW LEVEL SECURITY;


--
-- Name: loyal_patient_medication_history; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.loyal_patient_medication_history (
    record_id bigint NOT NULL,
    patient_record_id integer NOT NULL,
    prescription_no character varying,
    medication_barcode character varying NOT NULL,
    medication_purchase_date date,
    available_date date,
    quantity numeric,
    medication_usage text,
    report_diagnosis_no text,
    synced_at timestamp with time zone DEFAULT now() NOT NULL
);

ALTER TABLE ONLY public.loyal_patient_medication_history FORCE ROW LEVEL SECURITY;


--
-- Name: loyal_patient_medication_history_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.loyal_patient_medication_history ALTER COLUMN record_id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.loyal_patient_medication_history_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: loyal_patient_profile; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.loyal_patient_profile (
    patient_record_id integer NOT NULL,
    medication_history_synced_at timestamp with time zone,
    report_history_synced_at timestamp with time zone,
    last_sync_error text
);

ALTER TABLE ONLY public.loyal_patient_profile FORCE ROW LEVEL SECURITY;


--
-- Name: loyal_patient_report_history; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.loyal_patient_report_history (
    record_id bigint NOT NULL,
    patient_record_id integer NOT NULL,
    report_tracking_no character varying,
    report_no character varying,
    start_date date,
    end_date date,
    diagnosis text,
    synced_at timestamp with time zone DEFAULT now() NOT NULL
);

ALTER TABLE ONLY public.loyal_patient_report_history FORCE ROW LEVEL SECURITY;


--
-- Name: loyal_patient_report_history_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.loyal_patient_report_history ALTER COLUMN record_id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.loyal_patient_report_history_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: loyal_patient_report_sample_run; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.loyal_patient_report_sample_run (
    run_id uuid NOT NULL,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    completed_at timestamp with time zone,
    configured_sample_size integer NOT NULL,
    actual_sample_size integer DEFAULT 0 NOT NULL,
    threshold_ratio numeric(7,6) NOT NULL,
    delay_seconds numeric(8,3) NOT NULL,
    jitter_seconds numeric(8,3) NOT NULL,
    total_patient_count integer DEFAULT 0 NOT NULL,
    evaluated_patient_count integer DEFAULT 0 CONSTRAINT loyal_patient_report_sample_ru_evaluated_patient_count_not_null NOT NULL,
    failed_patient_count integer DEFAULT 0 NOT NULL,
    patient_with_report_count integer DEFAULT 0 CONSTRAINT loyal_patient_report_sample__patient_with_report_count_not_null NOT NULL,
    loyal_sample_count integer DEFAULT 0 NOT NULL,
    sample_loyal_ratio numeric(7,6),
    estimated_loyal_patient_count integer,
    status character varying(24) DEFAULT 'RUNNING'::character varying NOT NULL,
    error text,
    minimum_report_evidence integer DEFAULT 3 CONSTRAINT loyal_patient_report_sample_ru_minimum_report_evidence_not_null NOT NULL,
    hybrid_score_threshold numeric(6,2) DEFAULT 60 NOT NULL,
    family_loyal_bonus numeric(6,2) DEFAULT 65 NOT NULL,
    family_additional_loyal_bonus numeric(6,2) DEFAULT 5 CONSTRAINT loyal_patient_report_sample_family_additional_loyal_bo_not_null NOT NULL,
    family_unloyal_penalty numeric(6,2) DEFAULT 15 NOT NULL,
    wear_off_enabled boolean DEFAULT true NOT NULL,
    wear_off_days integer DEFAULT 30 NOT NULL,
    evidence_source character varying(32) DEFAULT 'MEDICATION_HISTORY'::character varying NOT NULL,
    cohort_key character varying(64) DEFAULT 'LOYALTY_BENCHMARK_V1'::character varying NOT NULL,
    mechanism1_loyal_count integer DEFAULT 0 NOT NULL,
    mechanism1_loyal_ratio numeric(7,6),
    mechanism1_estimated_count integer,
    mechanism2_loyal_count integer DEFAULT 0 NOT NULL,
    mechanism2_loyal_ratio numeric(7,6),
    mechanism2_estimated_count integer,
    mechanism3_loyal_count integer DEFAULT 0 NOT NULL,
    mechanism3_loyal_ratio numeric(7,6),
    mechanism3_estimated_count integer
);

ALTER TABLE ONLY public.loyal_patient_report_sample_run FORCE ROW LEVEL SECURITY;


--
-- Name: loyal_patient_report_supply_evidence; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.loyal_patient_report_supply_evidence (
    run_id uuid NOT NULL,
    patient_record_id integer NOT NULL,
    patient_tc_identity_no character varying(11) CONSTRAINT loyal_patient_report_supply_evi_patient_tc_identity_no_not_null NOT NULL,
    total_report_count integer,
    supplied_report_count integer,
    supply_ratio numeric(7,6),
    is_loyal boolean,
    evaluated_at timestamp with time zone DEFAULT now() NOT NULL,
    error text,
    has_report_evidence boolean,
    is_report_decisive boolean,
    frequency_score numeric(6,2),
    report_score numeric(6,2),
    family_score numeric(6,2),
    hybrid_score numeric(6,2),
    mechanism3_is_loyal boolean,
    loyal_family_member_count integer,
    explicitly_unloyal_family_member_count integer,
    decision_reason text,
    evidence_source character varying(32) DEFAULT 'MEDICATION_HISTORY'::character varying NOT NULL,
    mechanism1_is_loyal boolean,
    mechanism2_is_loyal boolean
);

ALTER TABLE ONLY public.loyal_patient_report_supply_evidence FORCE ROW LEVEL SECURITY;


--
-- Name: manual_movements; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.manual_movements (
    record_id integer NOT NULL,
    movement_direction character varying NOT NULL,
    its_notification_enabled boolean DEFAULT true NOT NULL,
    its_notification_id character varying,
    user_id character varying NOT NULL,
    note character varying,
    "timestamp" timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    CONSTRAINT ck_manual_movements_direction CHECK (((movement_direction)::text = ANY (ARRAY[('IN'::character varying)::text, ('OUT'::character varying)::text])))
);

ALTER TABLE ONLY public.manual_movements FORCE ROW LEVEL SECURITY;


--
-- Name: manual_movements_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.manual_movements ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.manual_movements_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: patient; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.patient (
    tc_identity_no character varying,
    first_name character varying,
    last_name character varying,
    relationship_group_code character varying,
    family_doctor_id character varying,
    note text,
    address text,
    phone_numbers character varying[],
    email_addresses character varying[],
    debt_amount numeric DEFAULT 0.00,
    allow_debt boolean DEFAULT true,
    maximum_debt numeric DEFAULT 1500,
    sicil_no character varying,
    date_of_birth date,
    photo bytea,
    gender character varying,
    debt_credit_score numeric DEFAULT 0,
    alive boolean DEFAULT true,
    consigned_qr_codes character varying[],
    patient_type character varying,
    insurance_type character varying,
    coverage_type character varying,
    record_id integer NOT NULL,
    "isForeignCitizen" boolean DEFAULT false NOT NULL,
    kurum_id integer,
    family_group_id integer,
    is_loyal boolean DEFAULT false NOT NULL,
    is_loyal_mechanism1 boolean DEFAULT false NOT NULL,
    is_loyal_mechanism2 boolean DEFAULT false NOT NULL,
    is_loyal_mechanism3 boolean DEFAULT false NOT NULL,
    loyalty_mechanism3_state smallint DEFAULT 0 NOT NULL,
    loyalty_mechanism3_score numeric(6,2),
    loyalty_mechanism3_evaluated_at timestamp with time zone,
    loyalty_report_ever_qualified boolean DEFAULT false NOT NULL,
    loyalty_report_evidence_count integer DEFAULT 0 NOT NULL,
    loyalty_unloyal_since timestamp with time zone,
    prescription_count integer DEFAULT 0 NOT NULL,
    loyalty_unloyal_strikes integer DEFAULT 0 NOT NULL,
    CONSTRAINT patient_loyalty_unloyal_strikes_check CHECK (((loyalty_unloyal_strikes >= 0) AND (loyalty_unloyal_strikes <= 3)))
);


--
-- Name: patient_debt_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.patient_debt_log (
    log_id bigint NOT NULL,
    patient_record_id integer NOT NULL,
    reference_code character varying NOT NULL,
    user_id character varying NOT NULL,
    "timestamp" timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    debt_change numeric(12,2) NOT NULL,
    debt_direction character(1) NOT NULL,
    CONSTRAINT patient_debt_log_debt_change_check CHECK ((debt_change > (0)::numeric)),
    CONSTRAINT patient_debt_log_debt_direction_check CHECK ((debt_direction = ANY (ARRAY['+'::bpchar, '-'::bpchar])))
);

ALTER TABLE ONLY public.patient_debt_log FORCE ROW LEVEL SECURITY;


--
-- Name: patient_debt_log_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.patient_debt_log ALTER COLUMN log_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.patient_debt_log_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: patient_identity; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.patient_identity (
    tc_identity_no character varying NOT NULL,
    patient_record_id integer NOT NULL
);


--
-- Name: patient_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.patient_log (
    log_id bigint NOT NULL,
    record_key character varying NOT NULL,
    operation character varying(6) NOT NULL,
    old_data jsonb,
    new_data jsonb,
    user_id character varying NOT NULL,
    "timestamp" timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    CONSTRAINT patient_log_operation_check CHECK (((operation)::text = ANY (ARRAY[('INSERT'::character varying)::text, ('UPDATE'::character varying)::text, ('DELETE'::character varying)::text])))
);

ALTER TABLE ONLY public.patient_log FORCE ROW LEVEL SECURITY;


--
-- Name: patient_log_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.patient_log ALTER COLUMN log_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.patient_log_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: patient_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.patient ALTER COLUMN record_id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.patient_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: payementTypes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."payementTypes" (
    record_id smallint NOT NULL,
    payment_type character varying NOT NULL
);


--
-- Name: pharmacy; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.pharmacy (
    gln_code character varying,
    pharmacy_name character varying,
    pharmacist_name character varying,
    medicines_in_transit character varying[],
    debt_balance numeric(12,2) DEFAULT 0 NOT NULL,
    address text,
    district character varying,
    city character varying,
    postal_code character varying,
    phone_numbers character varying[],
    email_addresses character varying[],
    tax_office character varying,
    tax_no character varying,
    uts_company_numbers character varying[],
    record_id integer NOT NULL
);


--
-- Name: pharmacy_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.pharmacy_log (
    log_id bigint NOT NULL,
    record_key character varying NOT NULL,
    operation character varying(6) NOT NULL,
    old_data jsonb,
    new_data jsonb,
    user_id character varying NOT NULL,
    "timestamp" timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    CONSTRAINT pharmacy_log_operation_check CHECK (((operation)::text = ANY (ARRAY[('INSERT'::character varying)::text, ('UPDATE'::character varying)::text, ('DELETE'::character varying)::text])))
);

ALTER TABLE ONLY public.pharmacy_log FORCE ROW LEVEL SECURITY;


--
-- Name: pharmacy_log_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.pharmacy_log ALTER COLUMN log_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.pharmacy_log_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: pharmacy_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.pharmacy ALTER COLUMN record_id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.pharmacy_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: pharmacy_schema_migration; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.pharmacy_schema_migration (
    migration_id character varying NOT NULL,
    applied_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: prescription; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.prescription (
    record_id integer NOT NULL,
    e_prescription_no character varying,
    protocol_no character varying,
    facility_code character varying,
    prescription_date date,
    medication_purchase_date date,
    has_qr_codes boolean,
    is_sequential_distribution boolean,
    patient_tc_identity_no character varying,
    patient_type character varying,
    patient_insurance_type character varying,
    patient_coverage_type character varying,
    recipient_tc_identity_no character varying,
    recipient_relationship character varying,
    doctor_diploma_registration_no character varying,
    registered_content character varying[],
    final_content character varying[],
    prescription_note text,
    prescription_copayment_cash numeric,
    prescription_copayment_salary numeric,
    examination_copayment_cash numeric,
    examination_copayment_salary numeric,
    medication_copayment numeric,
    equivalent_price_difference numeric,
    institution_total_payment numeric,
    patient_total_payment numeric,
    prescription_total numeric,
    qr_codes character varying[],
    medula_prescription_no character varying,
    saved_by_user_id character varying,
    saved_at timestamp with time zone DEFAULT now() NOT NULL,
    prescription_uid uuid NOT NULL,
    version_number integer NOT NULL,
    medula_filter_date date,
    imported_from_medula boolean DEFAULT false NOT NULL,
    identity_fingerprint character varying,
    list_status_code character varying,
    list_barcode_status character varying,
    list_medical_report character varying,
    list_document_status character varying,
    is_deleted boolean DEFAULT false NOT NULL,
    state_fingerprint character varying,
    medula_page_number integer,
    medula_row_index integer,
    doctor_branch_id integer,
    icd10_diagnoses character varying[],
    imported_from_farmassist boolean DEFAULT false NOT NULL,
    invoice_type_code smallint,
    qr_status_code smallint,
    prescription_type_code smallint,
    prescription_subtype_code smallint,
    provision_type_code smallint,
    doctor_certificate character varying,
    CONSTRAINT ck_prescription_list_status_code CHECK (((list_status_code IS NULL) OR ((list_status_code)::text = ANY (ARRAY[('I'::character varying)::text, ('K'::character varying)::text])))),
    CONSTRAINT ck_prescription_provision_type_code CHECK (((provision_type_code >= 1) AND (provision_type_code <= 8))),
    CONSTRAINT ck_prescription_qr_status_code CHECK (((qr_status_code >= 0) AND (qr_status_code <= 3))),
    CONSTRAINT ck_prescription_recipient_relationship CHECK (((recipient_relationship IS NULL) OR ((recipient_relationship)::text = ANY (ARRAY[('0'::character varying)::text, ('K'::character varying)::text, ('Y'::character varying)::text, ('D'::character varying)::text])))),
    CONSTRAINT ck_prescription_subtype_code CHECK (((prescription_subtype_code >= 1) AND (prescription_subtype_code <= 8))),
    CONSTRAINT ck_prescription_type_code CHECK (((prescription_type_code >= 1) AND (prescription_type_code <= 5)))
);

ALTER TABLE ONLY public.prescription FORCE ROW LEVEL SECURITY;


--
-- Name: prescription_invoice_type; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.prescription_invoice_type (
    invoice_type_code smallint NOT NULL,
    invoice_group_code smallint NOT NULL,
    invoice_group_name character varying NOT NULL,
    return_status_code smallint NOT NULL,
    return_status_name character varying NOT NULL,
    display_name character varying NOT NULL,
    CONSTRAINT ck_prescription_invoice_type_return_status CHECK (((return_status_code >= 0) AND (return_status_code <= 2)))
);


--
-- Name: prescription_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.prescription_log (
    log_id bigint NOT NULL,
    prescription_uid uuid NOT NULL,
    version_number integer NOT NULL,
    action_type character varying NOT NULL,
    update_datetime timestamp with time zone DEFAULT now() NOT NULL,
    user_id character varying,
    prescription_snapshot jsonb NOT NULL
);

ALTER TABLE ONLY public.prescription_log FORCE ROW LEVEL SECURITY;


--
-- Name: prescription_log_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.prescription_log ALTER COLUMN log_id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.prescription_log_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: prescription_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.prescription ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.prescription_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: prescription_sale_operations; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.prescription_sale_operations (
    operation_id uuid NOT NULL,
    prescription_uid uuid NOT NULL,
    sale_id integer NOT NULL,
    operation_type character varying NOT NULL,
    operation_status character varying NOT NULL,
    qr_codes character varying[] DEFAULT ARRAY[]::character varying[] NOT NULL,
    requested_by character varying,
    prepared_at timestamp with time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    remote_confirmed_at timestamp with time zone,
    local_completed_at timestamp with time zone,
    remote_message text,
    error_text text,
    untracked_qr_codes character varying[] DEFAULT ARRAY[]::character varying[] NOT NULL,
    CONSTRAINT ck_prescription_sale_operation_status CHECK (((operation_status)::text = ANY (ARRAY[('prepared'::character varying)::text, ('remote_confirmed'::character varying)::text, ('local_completed'::character varying)::text, ('failed'::character varying)::text, ('cancelled'::character varying)::text, ('reconciliation_required'::character varying)::text]))),
    CONSTRAINT ck_prescription_sale_operation_type CHECK (((operation_type)::text = ANY (ARRAY[('ITS_SALE'::character varying)::text, ('QR_FINALIZE'::character varying)::text, ('QR_FINALIZE_CANCEL'::character varying)::text, ('ITS_SALE_CANCEL'::character varying)::text])))
);

ALTER TABLE ONLY public.prescription_sale_operations FORCE ROW LEVEL SECURITY;


--
-- Name: qrActive; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."qrActive" (
    qr_code character varying NOT NULL,
    barcode character varying NOT NULL,
    expiry_date date,
    status character varying,
    purchase_cost numeric(12,2),
    reference_no character varying,
    reserved_prescription_uid uuid,
    reserved_at timestamp with time zone
);

ALTER TABLE ONLY public."qrActive" FORCE ROW LEVEL SECURITY;


--
-- Name: qrHanged; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."qrHanged" (
    qr_code character varying NOT NULL,
    takas_item_id integer NOT NULL,
    barcode character varying NOT NULL,
    expiry_date date,
    entry_cost numeric(12,2),
    debt_amount numeric(12,2) NOT NULL,
    reference_no character varying NOT NULL,
    hanged_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    CONSTRAINT "qrHanged_debt_amount_check" CHECK ((debt_amount >= (0)::numeric))
);

ALTER TABLE ONLY public."qrHanged" FORCE ROW LEVEL SECURITY;


--
-- Name: qrInactive; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."qrInactive" (
    qr_code character varying NOT NULL,
    barcode character varying NOT NULL,
    expiry_date date,
    entry_cost numeric(12,2),
    exit_price numeric(12,2),
    reference_no character varying
);

ALTER TABLE ONLY public."qrInactive" FORCE ROW LEVEL SECURITY;


--
-- Name: qrMovementTypes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."qrMovementTypes" (
    record_id smallint NOT NULL,
    code character varying NOT NULL,
    display_name character varying NOT NULL,
    is_entry boolean NOT NULL
);


--
-- Name: qrMovements_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."qrMovements" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."qrMovements_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: retail_its_operation_items; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.retail_its_operation_items (
    operation_id uuid NOT NULL,
    qr_code character varying NOT NULL,
    is_active boolean DEFAULT true NOT NULL
);

ALTER TABLE ONLY public.retail_its_operation_items FORCE ROW LEVEL SECURITY;


--
-- Name: retail_its_operations; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.retail_its_operations (
    operation_id uuid NOT NULL,
    operation_type character varying NOT NULL,
    operation_status character varying NOT NULL,
    pharmacy_gln character varying NOT NULL,
    request_payload jsonb NOT NULL,
    qr_codes character varying[] DEFAULT ARRAY[]::character varying[] NOT NULL,
    requested_by character varying NOT NULL,
    prepared_at timestamp with time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    remote_confirmed_at timestamp with time zone,
    local_completed_at timestamp with time zone,
    notification_id character varying,
    http_status integer,
    response_body text,
    fault_code character varying,
    fault_message text,
    error_text text,
    sale_id integer,
    refund_id integer,
    CONSTRAINT ck_retail_its_operation_reference CHECK (((((operation_type)::text = 'SALE'::text) AND (refund_id IS NULL)) OR (((operation_type)::text = 'SALE_CANCELLATION'::text) AND (sale_id IS NULL)))),
    CONSTRAINT ck_retail_its_operation_status CHECK (((operation_status)::text = ANY (ARRAY[('prepared'::character varying)::text, ('remote_confirmed'::character varying)::text, ('local_completed'::character varying)::text, ('definitely_not_sent'::character varying)::text, ('failed'::character varying)::text, ('reconciliation_required'::character varying)::text]))),
    CONSTRAINT ck_retail_its_operation_type CHECK (((operation_type)::text = ANY (ARRAY[('SALE'::character varying)::text, ('SALE_CANCELLATION'::character varying)::text])))
);

ALTER TABLE ONLY public.retail_its_operations FORCE ROW LEVEL SECURITY;


--
-- Name: sales; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sales (
    record_id integer NOT NULL,
    patient_id integer,
    its_no character varying,
    institution_amount numeric(12,2) DEFAULT 0 NOT NULL,
    total_amount numeric(12,2) NOT NULL,
    payement_finished boolean DEFAULT false NOT NULL,
    institution_payement_finished boolean DEFAULT false NOT NULL,
    debted boolean DEFAULT false NOT NULL,
    prescription_no character varying,
    user_no character varying,
    "timestamp" timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    invoice_no character varying,
    notes character varying,
    with_iskonto boolean DEFAULT false NOT NULL,
    marked boolean DEFAULT false NOT NULL,
    prescription_uid uuid,
    sale_status character varying DEFAULT 'completed'::character varying NOT NULL,
    patient_amount numeric(12,2) DEFAULT 0 NOT NULL,
    institution_secondary_amount numeric(12,2) DEFAULT 0 NOT NULL,
    owed_amount numeric(12,2) DEFAULT 0 NOT NULL,
    medula_sale_confirmed_at timestamp with time zone,
    prescription_finalized_at timestamp with time zone,
    cancelled_at timestamp with time zone,
    CONSTRAINT ck_sales_institution_amount CHECK ((institution_amount <= total_amount)),
    CONSTRAINT ck_sales_prescription_amounts CHECK (((patient_amount >= (0)::numeric) AND (institution_secondary_amount >= (0)::numeric) AND (owed_amount >= (0)::numeric))),
    CONSTRAINT ck_sales_sale_status CHECK (((sale_status)::text = ANY (ARRAY[('pending'::character varying)::text, ('medula_reported'::character varying)::text, ('completed'::character varying)::text, ('reconciliation_required'::character varying)::text, ('cancelled'::character varying)::text]))),
    CONSTRAINT sales_institution_amount_check CHECK ((institution_amount >= (0)::numeric)),
    CONSTRAINT sales_total_amount_check CHECK ((total_amount >= (0)::numeric))
);

ALTER TABLE ONLY public.sales FORCE ROW LEVEL SECURITY;


--
-- Name: salesPayements; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."salesPayements" (
    record_id integer NOT NULL,
    sale_id integer NOT NULL,
    payment_type smallint NOT NULL,
    amount numeric(12,2) NOT NULL,
    payment_role character varying,
    owed_amount numeric(12,2) DEFAULT 0 NOT NULL,
    payment_finished boolean DEFAULT true NOT NULL,
    settled_at timestamp with time zone,
    invoice_no character varying,
    cancelled_at timestamp with time zone,
    CONSTRAINT "ck_salesPayements_owed_amount" CHECK ((owed_amount >= (0)::numeric)),
    CONSTRAINT "salesPayements_amount_check" CHECK ((amount > (0)::numeric))
);

ALTER TABLE ONLY public."salesPayements" FORCE ROW LEVEL SECURITY;


--
-- Name: salesPayements_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."salesPayements" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."salesPayements_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: salesQrItems; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."salesQrItems" (
    record_id integer NOT NULL,
    sale_id integer NOT NULL,
    qr_code character varying NOT NULL,
    barcode character varying NOT NULL,
    gross_price numeric(12,2) NOT NULL,
    discount_amount numeric(12,2) DEFAULT 0 NOT NULL,
    exit_price numeric(12,2) NOT NULL,
    entry_cost numeric(12,2),
    item_source character varying DEFAULT 'retail'::character varying NOT NULL,
    inventory_tracked boolean DEFAULT true NOT NULL,
    CONSTRAINT "ck_salesQrItems_item_source" CHECK (((item_source)::text = ANY (ARRAY[('retail'::character varying)::text, ('prescription'::character varying)::text]))),
    CONSTRAINT "ck_salesQrItems_net_exit_price" CHECK ((exit_price = (gross_price - discount_amount))),
    CONSTRAINT "salesQrItems_check" CHECK (((discount_amount >= (0)::numeric) AND (discount_amount <= gross_price))),
    CONSTRAINT "salesQrItems_exit_price_check" CHECK ((exit_price >= (0)::numeric)),
    CONSTRAINT "salesQrItems_gross_price_check" CHECK ((gross_price >= (0)::numeric))
);

ALTER TABLE ONLY public."salesQrItems" FORCE ROW LEVEL SECURITY;


--
-- Name: salesQrItems_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."salesQrItems" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."salesQrItems_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: salesRefundItems; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."salesRefundItems" (
    record_id integer NOT NULL,
    refund_id integer NOT NULL,
    original_sale_id integer,
    qr_code character varying NOT NULL,
    barcode character varying NOT NULL,
    gross_price numeric(12,2) NOT NULL,
    discount_amount numeric(12,2) DEFAULT 0 NOT NULL,
    exit_price numeric(12,2) NOT NULL,
    entry_cost numeric(12,2),
    item_source character varying DEFAULT 'retail'::character varying NOT NULL,
    inventory_tracked boolean DEFAULT true NOT NULL,
    CONSTRAINT "ck_salesRefundItems_item_source" CHECK (((item_source)::text = ANY (ARRAY[('retail'::character varying)::text, ('prescription'::character varying)::text]))),
    CONSTRAINT "ck_salesRefundItems_net_exit_price" CHECK ((exit_price = (gross_price - discount_amount))),
    CONSTRAINT "ck_salesRefundItems_prescription_original" CHECK ((((item_source)::text <> 'prescription'::text) OR (original_sale_id IS NOT NULL))),
    CONSTRAINT "salesRefundItems_check" CHECK (((discount_amount >= (0)::numeric) AND (discount_amount <= gross_price))),
    CONSTRAINT "salesRefundItems_exit_price_check" CHECK ((exit_price >= (0)::numeric)),
    CONSTRAINT "salesRefundItems_gross_price_check" CHECK ((gross_price >= (0)::numeric))
);

ALTER TABLE ONLY public."salesRefundItems" FORCE ROW LEVEL SECURITY;


--
-- Name: salesRefundItems_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."salesRefundItems" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."salesRefundItems_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: salesRefundPayements; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."salesRefundPayements" (
    record_id integer NOT NULL,
    refund_id integer NOT NULL,
    payment_type smallint NOT NULL,
    amount numeric(12,2) NOT NULL,
    payment_role character varying,
    payment_finished boolean DEFAULT true NOT NULL,
    settled_at timestamp with time zone,
    CONSTRAINT ck_salesrefundpayements_supported_type CHECK ((payment_type = ANY (ARRAY[1, 2, 3, 7]))),
    CONSTRAINT "salesRefundPayements_amount_check" CHECK ((amount > (0)::numeric))
);

ALTER TABLE ONLY public."salesRefundPayements" FORCE ROW LEVEL SECURITY;


--
-- Name: salesRefundPayements_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."salesRefundPayements" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."salesRefundPayements_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: salesRefunds; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."salesRefunds" (
    record_id integer NOT NULL,
    patient_id integer,
    total_amount numeric(12,2) NOT NULL,
    payement_finished boolean DEFAULT false NOT NULL,
    user_no character varying,
    "timestamp" timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    discount_amount numeric(12,2) DEFAULT 0 NOT NULL,
    debt_reduction_amount numeric(12,2) DEFAULT 0 NOT NULL,
    marked boolean DEFAULT false NOT NULL,
    notes character varying,
    original_sale_id integer,
    prescription_uid uuid,
    refund_source character varying DEFAULT 'retail'::character varying NOT NULL,
    refund_status character varying DEFAULT 'completed'::character varying NOT NULL,
    patient_amount numeric(12,2) DEFAULT 0 NOT NULL,
    institution_amount numeric(12,2) DEFAULT 0 NOT NULL,
    institution_secondary_amount numeric(12,2) DEFAULT 0 NOT NULL,
    owed_amount numeric(12,2) DEFAULT 0 NOT NULL,
    historical_import boolean DEFAULT false NOT NULL,
    CONSTRAINT "ck_salesRefunds_prescription_amounts" CHECK (((patient_amount >= (0)::numeric) AND (institution_amount >= (0)::numeric) AND (institution_secondary_amount >= (0)::numeric) AND (owed_amount >= (0)::numeric))),
    CONSTRAINT "ck_salesRefunds_source" CHECK (((refund_source)::text = ANY (ARRAY[('retail'::character varying)::text, ('prescription'::character varying)::text]))),
    CONSTRAINT "ck_salesRefunds_status" CHECK (((refund_status)::text = ANY (ARRAY[('completed'::character varying)::text, ('reconciliation_required'::character varying)::text, ('cancelled'::character varying)::text]))),
    CONSTRAINT "salesRefunds_debt_reduction_amount_check" CHECK ((debt_reduction_amount >= (0)::numeric)),
    CONSTRAINT "salesRefunds_discount_amount_check" CHECK ((discount_amount >= (0)::numeric)),
    CONSTRAINT "salesRefunds_total_amount_check" CHECK ((total_amount >= (0)::numeric))
);

ALTER TABLE ONLY public."salesRefunds" FORCE ROW LEVEL SECURITY;


--
-- Name: salesRefunds_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."salesRefunds" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."salesRefunds_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: sales_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.sales ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.sales_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: scheduled_stock_movements; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.scheduled_stock_movements (
    record_id integer NOT NULL
);

ALTER TABLE ONLY public.scheduled_stock_movements FORCE ROW LEVEL SECURITY;


--
-- Name: stock_its_operation_items; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.stock_its_operation_items (
    operation_id uuid NOT NULL,
    qr_code character varying NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    result_code character varying,
    error_text text
);

ALTER TABLE ONLY public.stock_its_operation_items FORCE ROW LEVEL SECURITY;


--
-- Name: stock_its_operations; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.stock_its_operations (
    operation_id uuid NOT NULL,
    operation_type character varying NOT NULL,
    operation_status character varying NOT NULL,
    pharmacy_gln character varying NOT NULL,
    request_payload jsonb NOT NULL,
    qr_codes character varying[] DEFAULT ARRAY[]::character varying[] NOT NULL,
    requested_by character varying NOT NULL,
    source_reference character varying,
    prepared_at timestamp with time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    remote_confirmed_at timestamp with time zone,
    local_completed_at timestamp with time zone,
    notification_id character varying,
    http_status integer,
    response_body text,
    fault_code character varying,
    fault_message text,
    error_text text,
    manual_movement_id integer,
    CONSTRAINT ck_stock_its_operation_status CHECK (((operation_status)::text = ANY (ARRAY[('prepared'::character varying)::text, ('remote_confirmed'::character varying)::text, ('local_completed'::character varying)::text, ('definitely_not_sent'::character varying)::text, ('failed'::character varying)::text, ('reconciliation_required'::character varying)::text]))),
    CONSTRAINT ck_stock_its_operation_type CHECK (((operation_type)::text = ANY (ARRAY[('SALE'::character varying)::text, ('SALE_CANCELLATION'::character varying)::text, ('ACCEPTANCE'::character varying)::text])))
);

ALTER TABLE ONLY public.stock_its_operations FORCE ROW LEVEL SECURITY;


--
-- Name: takas; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.takas (
    record_id integer NOT NULL,
    pharmacy_id integer NOT NULL,
    direction character varying DEFAULT 'outgoing'::character varying NOT NULL,
    source_gln character varying(13),
    destination_gln character varying(13),
    status character varying DEFAULT 'pending_transfer'::character varying NOT NULL,
    debt_amount numeric(12,2) DEFAULT 0 NOT NULL,
    transfer_notification_id character varying(20),
    accept_notification_id character varying(20),
    cancel_notification_id character varying(20),
    user_no character varying,
    created_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    updated_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    transferred_at timestamp without time zone,
    accepted_at timestamp without time zone,
    cancelled_at timestamp without time zone,
    debt_applied_at timestamp without time zone,
    debt_reversed_at timestamp without time zone,
    last_status_checked_at timestamp without time zone,
    next_status_check_at timestamp without time zone,
    status_check_attempts integer DEFAULT 0 NOT NULL,
    last_status_error text,
    notes text,
    CONSTRAINT ck_takas_destination_gln CHECK (((destination_gln)::text ~ '^[0-9]{13}$'::text)),
    CONSTRAINT ck_takas_direction CHECK (((direction)::text = ANY (ARRAY[('outgoing'::character varying)::text, ('incoming'::character varying)::text]))),
    CONSTRAINT ck_takas_distinct_gln CHECK (((source_gln)::text <> (destination_gln)::text)),
    CONSTRAINT ck_takas_source_gln CHECK (((source_gln)::text ~ '^[0-9]{13}$'::text)),
    CONSTRAINT ck_takas_status CHECK (((status)::text = ANY (ARRAY[('pending_transfer'::character varying)::text, ('in_transit'::character varying)::text, ('partially_transferred'::character varying)::text, ('partially_accepted'::character varying)::text, ('accepted'::character varying)::text, ('partially_cancelled'::character varying)::text, ('cancelled'::character varying)::text, ('failed'::character varying)::text, ('reconciliation_required'::character varying)::text]))),
    CONSTRAINT takas_debt_amount_check CHECK ((debt_amount >= (0)::numeric)),
    CONSTRAINT takas_status_check_attempts_check CHECK ((status_check_attempts >= 0))
);

ALTER TABLE ONLY public.takas FORCE ROW LEVEL SECURITY;


--
-- Name: takasBarcodeItems; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."takasBarcodeItems" (
    record_id integer NOT NULL,
    takas_id integer NOT NULL,
    barcode character varying NOT NULL,
    entry_cost numeric(12,2),
    debt_amount numeric(12,2) NOT NULL,
    status character varying DEFAULT 'accepted'::character varying NOT NULL,
    CONSTRAINT ck_takasbarcodeitems_status CHECK (((status)::text = ANY (ARRAY[('pending_transfer'::character varying)::text, ('in_transit'::character varying)::text, ('accepted'::character varying)::text, ('transfer_cancelled'::character varying)::text, ('transfer_failed'::character varying)::text, ('reconciliation_required'::character varying)::text]))),
    CONSTRAINT "takasBarcodeItems_debt_amount_check" CHECK ((debt_amount >= (0)::numeric))
);

ALTER TABLE ONLY public."takasBarcodeItems" FORCE ROW LEVEL SECURITY;


--
-- Name: takasBarcodeItems_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."takasBarcodeItems" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."takasBarcodeItems_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: takasQrItems; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."takasQrItems" (
    record_id integer NOT NULL,
    takas_id integer NOT NULL,
    qr_code character varying NOT NULL,
    barcode character varying NOT NULL,
    gtin character varying(14) NOT NULL,
    serial_number character varying(20) NOT NULL,
    batch_number character varying(20) NOT NULL,
    expiry_date date NOT NULL,
    entry_cost numeric(12,2),
    debt_amount numeric(12,2) NOT NULL,
    status character varying DEFAULT 'pending_transfer'::character varying NOT NULL,
    transfer_result_code character varying(5),
    accept_result_code character varying(5),
    cancel_result_code character varying(5),
    last_status_result_code character varying(5),
    last_status_gln1 character varying(13),
    last_status_gln2 character varying(13),
    last_checked_at timestamp without time zone,
    status_changed_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    last_error text,
    CONSTRAINT ck_takasqritems_gtin CHECK (((gtin)::text ~ '^[0-9]{14}$'::text)),
    CONSTRAINT ck_takasqritems_status CHECK (((status)::text = ANY (ARRAY[('pending_transfer'::character varying)::text, ('in_transit'::character varying)::text, ('accepted'::character varying)::text, ('transfer_cancelled'::character varying)::text, ('transfer_failed'::character varying)::text, ('reconciliation_required'::character varying)::text]))),
    CONSTRAINT "takasQrItems_debt_amount_check" CHECK ((debt_amount >= (0)::numeric))
);

ALTER TABLE ONLY public."takasQrItems" FORCE ROW LEVEL SECURITY;


--
-- Name: takasQrItems_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."takasQrItems" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."takasQrItems_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: takas_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.takas ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.takas_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: unnotified_its_movements; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.unnotified_its_movements (
    record_id integer NOT NULL,
    qr_code character varying NOT NULL,
    reference_no character varying NOT NULL,
    movement_direction character varying NOT NULL,
    notification_type character varying NOT NULL,
    is_corrected boolean DEFAULT false NOT NULL,
    created_by character varying NOT NULL,
    created_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    corrected_by character varying,
    corrected_at timestamp without time zone,
    correction_notification_id character varying,
    paired_record_id integer,
    CONSTRAINT ck_unnotified_its_correction_state CHECK ((((NOT is_corrected) AND (corrected_at IS NULL)) OR is_corrected)),
    CONSTRAINT ck_unnotified_its_direction CHECK (((movement_direction)::text = ANY (ARRAY[('IN'::character varying)::text, ('OUT'::character varying)::text]))),
    CONSTRAINT ck_unnotified_its_notification_type CHECK (((notification_type)::text = ANY (ARRAY[('SALE'::character varying)::text, ('SALE_CANCELLATION'::character varying)::text, ('ACCEPTANCE'::character varying)::text])))
);

ALTER TABLE ONLY public.unnotified_its_movements FORCE ROW LEVEL SECURITY;


--
-- Name: unnotified_its_movements_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.unnotified_its_movements ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.unnotified_its_movements_record_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: users; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.users (
    user_id character varying NOT NULL,
    tc_identity_no character varying,
    first_name character varying,
    last_name character varying,
    phone_number character varying,
    gender character varying,
    age integer,
    date_of_birth date,
    photo bytea,
    permisson_level character varying,
    entry_date date,
    exit_date date,
    user_points integer,
    status boolean,
    password character varying,
    session_active boolean,
    CONSTRAINT users_admin_only CHECK (((user_id)::text = 'admin'::text))
);


--
-- Name: users_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.users_log (
    log_id bigint NOT NULL,
    record_key character varying NOT NULL,
    operation character varying(6) NOT NULL,
    old_data jsonb,
    new_data jsonb,
    user_id character varying NOT NULL,
    "timestamp" timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    CONSTRAINT users_log_operation_check CHECK (((operation)::text = ANY (ARRAY[('INSERT'::character varying)::text, ('UPDATE'::character varying)::text, ('DELETE'::character varying)::text])))
);

ALTER TABLE ONLY public.users_log FORCE ROW LEVEL SECURITY;


--
-- Name: users_log_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.users_log ALTER COLUMN log_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public.users_log_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: warningIcons; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."warningIcons" (
    record_id integer NOT NULL,
    warning_header character varying NOT NULL,
    source_url text NOT NULL,
    icon bytea NOT NULL,
    content_type character varying DEFAULT 'image/svg+xml'::character varying NOT NULL,
    sample_barcode character varying,
    downloaded_at timestamp with time zone DEFAULT CURRENT_TIMESTAMP NOT NULL
);


--
-- Name: TABLE "warningIcons"; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public."warningIcons" IS 'Locally cached medication warning icons, linked to warning headers and their source variants.';


--
-- Name: COLUMN "warningIcons".sample_barcode; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public."warningIcons".sample_barcode IS 'Representative barcode row from which the warning header and source URL were selected.';


--
-- Name: warningIcons_record_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."warningIcons" ALTER COLUMN record_id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."warningIcons_record_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: addresses addresses_compact_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.addresses
    ADD CONSTRAINT addresses_compact_pkey PRIMARY KEY (record_id);


--
-- Name: barcodeActive barcodeActive_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."barcodeActive"
    ADD CONSTRAINT "barcodeActive_pkey" PRIMARY KEY (barcode);


--
-- Name: barcodeAnalysis barcodeAnalysis_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."barcodeAnalysis"
    ADD CONSTRAINT "barcodeAnalysis_pkey" PRIMARY KEY (record_id);


--
-- Name: barcodeCampaign barcodeCampaign_pkey1; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."barcodeCampaign"
    ADD CONSTRAINT "barcodeCampaign_pkey1" PRIMARY KEY (campaign_id);


--
-- Name: barcodePriceHistory barcodePriceHistory_pkey1; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."barcodePriceHistory"
    ADD CONSTRAINT "barcodePriceHistory_pkey1" PRIMARY KEY (history_id);


--
-- Name: barcodePrices barcodePrices_pkey1; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."barcodePrices"
    ADD CONSTRAINT "barcodePrices_pkey1" PRIMARY KEY (record_reference_no);


--
-- Name: barcode barcode_pkey1; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.barcode
    ADD CONSTRAINT barcode_pkey1 PRIMARY KEY (record_id);


--
-- Name: branches branches_display_order_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.branches
    ADD CONSTRAINT branches_display_order_key UNIQUE (display_order);


--
-- Name: branches branches_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.branches
    ADD CONSTRAINT branches_pkey PRIMARY KEY (branch_id);


--
-- Name: consignmentSaleItems consignmentSaleItems_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."consignmentSaleItems"
    ADD CONSTRAINT "consignmentSaleItems_pkey" PRIMARY KEY (record_id);


--
-- Name: consignmentSalePayements consignmentSalePayements_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."consignmentSalePayements"
    ADD CONSTRAINT "consignmentSalePayements_pkey" PRIMARY KEY (record_id);


--
-- Name: consignmentSales consignmentSales_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."consignmentSales"
    ADD CONSTRAINT "consignmentSales_pkey" PRIMARY KEY (record_id);


--
-- Name: depot_log depot_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.depot_log
    ADD CONSTRAINT depot_log_pkey PRIMARY KEY (log_id);


--
-- Name: depot depot_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.depot
    ADD CONSTRAINT depot_pkey PRIMARY KEY (record_id);


--
-- Name: doctor_log doctor_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.doctor_log
    ADD CONSTRAINT doctor_log_pkey PRIMARY KEY (log_id);


--
-- Name: doctor doctor_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.doctor
    ADD CONSTRAINT doctor_pkey PRIMARY KEY (record_id);


--
-- Name: documents documents_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.documents
    ADD CONSTRAINT documents_pkey PRIMARY KEY (barcode);


--
-- Name: faculty_log faculty_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.faculty_log
    ADD CONSTRAINT faculty_log_pkey PRIMARY KEY (log_id);


--
-- Name: faculty faculty_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.faculty
    ADD CONSTRAINT faculty_pkey PRIMARY KEY (record_id);


--
-- Name: faculty_type faculty_type_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.faculty_type
    ADD CONSTRAINT faculty_type_pkey PRIMARY KEY (faculty_type_id);


--
-- Name: family_group family_group_family_group_name_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.family_group
    ADD CONSTRAINT family_group_family_group_name_key UNIQUE (family_group_name);


--
-- Name: family_group_log family_group_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.family_group_log
    ADD CONSTRAINT family_group_log_pkey PRIMARY KEY (log_id);


--
-- Name: family_group family_group_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.family_group
    ADD CONSTRAINT family_group_pkey PRIMARY KEY (record_id);


--
-- Name: icd10_diagnosis icd10_diagnosis_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.icd10_diagnosis
    ADD CONSTRAINT icd10_diagnosis_pkey PRIMARY KEY (code);


--
-- Name: invoice_incremental_sync_items invoice_incremental_sync_items_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_incremental_sync_items
    ADD CONSTRAINT invoice_incremental_sync_items_pkey PRIMARY KEY (run_id, type_key, document_uuid);


--
-- Name: invoice_incremental_sync_state invoice_incremental_sync_state_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_incremental_sync_state
    ADD CONSTRAINT invoice_incremental_sync_state_pkey PRIMARY KEY (source_key);


--
-- Name: invoice_line_qr_codes invoice_line_qr_codes_line_code_unique; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_line_qr_codes
    ADD CONSTRAINT invoice_line_qr_codes_line_code_unique UNIQUE (invoice_line_id, qr_code);


--
-- Name: invoice_line_qr_codes invoice_line_qr_codes_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_line_qr_codes
    ADD CONSTRAINT invoice_line_qr_codes_pkey PRIMARY KEY (record_id);


--
-- Name: invoice_lines invoice_lines_compact_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_lines
    ADD CONSTRAINT invoice_lines_compact_pkey PRIMARY KEY (record_id);


--
-- Name: invoice_stock_log_invoices invoice_stock_log_invoices_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_stock_log_invoices
    ADD CONSTRAINT invoice_stock_log_invoices_pkey PRIMARY KEY (invoice_stock_log_id, invoice_uuid);


--
-- Name: invoice_stock_logs invoice_stock_logs_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_stock_logs
    ADD CONSTRAINT invoice_stock_logs_pkey PRIMARY KEY (record_id);


--
-- Name: invoice_stock_logs invoice_stock_logs_reference_code_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_stock_logs
    ADD CONSTRAINT invoice_stock_logs_reference_code_key UNIQUE (reference_code);


--
-- Name: invoices invoices_compact_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoices
    ADD CONSTRAINT invoices_compact_pkey PRIMARY KEY (invoice_uuid);


--
-- Name: kurum kurum_kurum_code_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.kurum
    ADD CONSTRAINT kurum_kurum_code_key UNIQUE (kurum_code);


--
-- Name: kurum_log kurum_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.kurum_log
    ADD CONSTRAINT kurum_log_pkey PRIMARY KEY (log_id);


--
-- Name: kurum kurum_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.kurum
    ADD CONSTRAINT kurum_pkey PRIMARY KEY (record_id);


--
-- Name: loyal_patient_benchmark_cohort loyal_patient_benchmark_cohort_cohort_key_cohort_position_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_benchmark_cohort
    ADD CONSTRAINT loyal_patient_benchmark_cohort_cohort_key_cohort_position_key UNIQUE (cohort_key, cohort_position);


--
-- Name: loyal_patient_benchmark_cohort loyal_patient_benchmark_cohort_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_benchmark_cohort
    ADD CONSTRAINT loyal_patient_benchmark_cohort_pkey PRIMARY KEY (cohort_key, patient_record_id);


--
-- Name: loyal_patient_medication_history loyal_patient_medication_history_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_medication_history
    ADD CONSTRAINT loyal_patient_medication_history_pkey PRIMARY KEY (record_id);


--
-- Name: loyal_patient_profile loyal_patient_profile_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_profile
    ADD CONSTRAINT loyal_patient_profile_pkey PRIMARY KEY (patient_record_id);


--
-- Name: loyal_patient_report_history loyal_patient_report_history_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_report_history
    ADD CONSTRAINT loyal_patient_report_history_pkey PRIMARY KEY (record_id);


--
-- Name: loyal_patient_report_sample_run loyal_patient_report_sample_run_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_report_sample_run
    ADD CONSTRAINT loyal_patient_report_sample_run_pkey PRIMARY KEY (run_id);


--
-- Name: loyal_patient_report_supply_evidence loyal_patient_report_supply_evidence_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_report_supply_evidence
    ADD CONSTRAINT loyal_patient_report_supply_evidence_pkey PRIMARY KEY (run_id, patient_record_id);


--
-- Name: manual_movements manual_movements_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.manual_movements
    ADD CONSTRAINT manual_movements_pkey PRIMARY KEY (record_id);


--
-- Name: patient_debt_log patient_debt_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient_debt_log
    ADD CONSTRAINT patient_debt_log_pkey PRIMARY KEY (log_id);


--
-- Name: patient_identity patient_identity_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient_identity
    ADD CONSTRAINT patient_identity_pkey PRIMARY KEY (tc_identity_no);


--
-- Name: patient_log patient_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient_log
    ADD CONSTRAINT patient_log_pkey PRIMARY KEY (log_id);


--
-- Name: patient patient_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient
    ADD CONSTRAINT patient_pkey PRIMARY KEY (record_id);


--
-- Name: payementTypes payementTypes_payment_type_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."payementTypes"
    ADD CONSTRAINT "payementTypes_payment_type_key" UNIQUE (payment_type);


--
-- Name: payementTypes payementTypes_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."payementTypes"
    ADD CONSTRAINT "payementTypes_pkey" PRIMARY KEY (record_id);


--
-- Name: pharmacy_log pharmacy_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pharmacy_log
    ADD CONSTRAINT pharmacy_log_pkey PRIMARY KEY (log_id);


--
-- Name: pharmacy pharmacy_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pharmacy
    ADD CONSTRAINT pharmacy_pkey PRIMARY KEY (record_id);


--
-- Name: pharmacy_schema_migration pharmacy_schema_migration_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pharmacy_schema_migration
    ADD CONSTRAINT pharmacy_schema_migration_pkey PRIMARY KEY (migration_id);


--
-- Name: prescription_invoice_type prescription_invoice_type_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription_invoice_type
    ADD CONSTRAINT prescription_invoice_type_pkey PRIMARY KEY (invoice_type_code);


--
-- Name: prescription_log prescription_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription_log
    ADD CONSTRAINT prescription_log_pkey PRIMARY KEY (log_id);


--
-- Name: prescription prescription_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription
    ADD CONSTRAINT prescription_pkey PRIMARY KEY (record_id);


--
-- Name: prescription_sale_operations prescription_sale_operations_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription_sale_operations
    ADD CONSTRAINT prescription_sale_operations_pkey PRIMARY KEY (operation_id);


--
-- Name: qrActive qrActive_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."qrActive"
    ADD CONSTRAINT "qrActive_pkey" PRIMARY KEY (qr_code);


--
-- Name: qrHanged qrHanged_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."qrHanged"
    ADD CONSTRAINT "qrHanged_pkey" PRIMARY KEY (qr_code);


--
-- Name: qrHanged qrHanged_takas_item_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."qrHanged"
    ADD CONSTRAINT "qrHanged_takas_item_id_key" UNIQUE (takas_item_id);


--
-- Name: qrInactive qrInactive_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."qrInactive"
    ADD CONSTRAINT "qrInactive_pkey" PRIMARY KEY (qr_code);


--
-- Name: qrMovementTypes qrMovementTypes_code_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."qrMovementTypes"
    ADD CONSTRAINT "qrMovementTypes_code_key" UNIQUE (code);


--
-- Name: qrMovementTypes qrMovementTypes_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."qrMovementTypes"
    ADD CONSTRAINT "qrMovementTypes_pkey" PRIMARY KEY (record_id);


--
-- Name: qrMovements qrMovements_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."qrMovements"
    ADD CONSTRAINT "qrMovements_pkey" PRIMARY KEY (record_id);


--
-- Name: retail_its_operation_items retail_its_operation_items_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retail_its_operation_items
    ADD CONSTRAINT retail_its_operation_items_pkey PRIMARY KEY (operation_id, qr_code);


--
-- Name: retail_its_operations retail_its_operations_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retail_its_operations
    ADD CONSTRAINT retail_its_operations_pkey PRIMARY KEY (operation_id);


--
-- Name: salesPayements salesPayements_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesPayements"
    ADD CONSTRAINT "salesPayements_pkey" PRIMARY KEY (record_id);


--
-- Name: salesQrItems salesQrItems_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesQrItems"
    ADD CONSTRAINT "salesQrItems_pkey" PRIMARY KEY (record_id);


--
-- Name: salesRefundItems salesRefundItems_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesRefundItems"
    ADD CONSTRAINT "salesRefundItems_pkey" PRIMARY KEY (record_id);


--
-- Name: salesRefundPayements salesRefundPayements_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesRefundPayements"
    ADD CONSTRAINT "salesRefundPayements_pkey" PRIMARY KEY (record_id);


--
-- Name: salesRefunds salesRefunds_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesRefunds"
    ADD CONSTRAINT "salesRefunds_pkey" PRIMARY KEY (record_id);


--
-- Name: sales sales_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales
    ADD CONSTRAINT sales_pkey PRIMARY KEY (record_id);


--
-- Name: scheduled_stock_movements scheduled_stock_movements_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.scheduled_stock_movements
    ADD CONSTRAINT scheduled_stock_movements_pkey PRIMARY KEY (record_id);


--
-- Name: stock_its_operation_items stock_its_operation_items_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.stock_its_operation_items
    ADD CONSTRAINT stock_its_operation_items_pkey PRIMARY KEY (operation_id, qr_code);


--
-- Name: stock_its_operations stock_its_operations_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.stock_its_operations
    ADD CONSTRAINT stock_its_operations_pkey PRIMARY KEY (operation_id);


--
-- Name: takasBarcodeItems takasBarcodeItems_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."takasBarcodeItems"
    ADD CONSTRAINT "takasBarcodeItems_pkey" PRIMARY KEY (record_id);


--
-- Name: takasQrItems takasQrItems_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."takasQrItems"
    ADD CONSTRAINT "takasQrItems_pkey" PRIMARY KEY (record_id);


--
-- Name: takas takas_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.takas
    ADD CONSTRAINT takas_pkey PRIMARY KEY (record_id);


--
-- Name: unnotified_its_movements unnotified_its_movements_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unnotified_its_movements
    ADD CONSTRAINT unnotified_its_movements_pkey PRIMARY KEY (record_id);


--
-- Name: doctor uq_doctor_diploma_registration_no; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.doctor
    ADD CONSTRAINT uq_doctor_diploma_registration_no UNIQUE (diploma_registration_no);


--
-- Name: faculty uq_faculty_faculty_code; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.faculty
    ADD CONSTRAINT uq_faculty_faculty_code UNIQUE (faculty_code);


--
-- Name: takasQrItems uq_takasqritems_takas_qr; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."takasQrItems"
    ADD CONSTRAINT uq_takasqritems_takas_qr UNIQUE (takas_id, qr_code);


--
-- Name: users_log users_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.users_log
    ADD CONSTRAINT users_log_pkey PRIMARY KEY (log_id);


--
-- Name: users users_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_pkey PRIMARY KEY (user_id);


--
-- Name: prescription_invoice_type ux_prescription_invoice_type_variant; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription_invoice_type
    ADD CONSTRAINT ux_prescription_invoice_type_variant UNIQUE (invoice_group_code, return_status_code);


--
-- Name: warningIcons warningIcons_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."warningIcons"
    ADD CONSTRAINT "warningIcons_pkey" PRIMARY KEY (record_id);


--
-- Name: warningIcons warning_icons_header_url_unique; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."warningIcons"
    ADD CONSTRAINT warning_icons_header_url_unique UNIQUE (warning_header, source_url);


--
-- Name: addresses_gln_unique_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX addresses_gln_unique_idx ON public.addresses USING btree (btrim(gln)) WHERE (btrim(gln) <> ''::text);


--
-- Name: addresses_identity_no_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX addresses_identity_no_idx ON public.addresses USING btree (identity_no);


--
-- Name: addresses_party_name_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX addresses_party_name_idx ON public.addresses USING btree (party_name);


--
-- Name: barcodeCampaign_record_reference_no_start_date_end_date_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "barcodeCampaign_record_reference_no_start_date_end_date_idx" ON public."barcodeCampaign" USING btree (record_reference_no, start_date, end_date);


--
-- Name: barcodeCampaign_warehouse_gln_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "barcodeCampaign_warehouse_gln_idx" ON public."barcodeCampaign" USING btree (warehouse_gln);


--
-- Name: barcodePriceHistory_record_reference_no_effective_date_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "barcodePriceHistory_record_reference_no_effective_date_idx" ON public."barcodePriceHistory" USING btree (record_reference_no, effective_date DESC);


--
-- Name: barcode_barcode_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX barcode_barcode_idx ON public.barcode USING btree (barcode);


--
-- Name: barcode_source_product_id_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX barcode_source_product_id_idx ON public.barcode USING btree (source_product_id);


--
-- Name: idx_qrMovements_type_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "idx_qrMovements_type_date" ON public."qrMovements" USING btree (movement_type, movement_date DESC);


--
-- Name: idx_qractive_barcode_expiry; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_qractive_barcode_expiry ON public."qrActive" USING btree (barcode, expiry_date);


--
-- Name: idx_qrinactive_barcode; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_qrinactive_barcode ON public."qrInactive" USING btree (barcode);


--
-- Name: idx_qrmovements_movement_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_qrmovements_movement_date ON public."qrMovements" USING btree (movement_date DESC);


--
-- Name: idx_qrmovements_qr_code_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_qrmovements_qr_code_date ON public."qrMovements" USING btree (qr_code, movement_date DESC);


--
-- Name: invoice_incremental_sync_items_pending_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoice_incremental_sync_items_pending_idx ON public.invoice_incremental_sync_items USING btree (run_id, status, received_at, issue_date);


--
-- Name: invoice_line_qr_codes_gtin_serial_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoice_line_qr_codes_gtin_serial_idx ON public.invoice_line_qr_codes USING btree (gtin, serial_number);


--
-- Name: invoice_line_qr_codes_line_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoice_line_qr_codes_line_idx ON public.invoice_line_qr_codes USING btree (invoice_line_id);


--
-- Name: invoice_line_qr_codes_qr_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoice_line_qr_codes_qr_idx ON public.invoice_line_qr_codes USING btree (qr_code);


--
-- Name: invoice_lines_invoice_uuid_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoice_lines_invoice_uuid_idx ON public.invoice_lines USING btree (invoice_uuid);


--
-- Name: invoice_stock_log_invoices_invoice_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoice_stock_log_invoices_invoice_idx ON public.invoice_stock_log_invoices USING btree (invoice_uuid, invoice_stock_log_id);


--
-- Name: invoice_stock_logs_invoice_time_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoice_stock_logs_invoice_time_idx ON public.invoice_stock_logs USING btree (invoice_uuid, occurred_at DESC, record_id DESC);


--
-- Name: invoice_stock_logs_movement_time_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoice_stock_logs_movement_time_idx ON public.invoice_stock_logs USING btree (movement_type_id, occurred_at DESC, record_id DESC);


--
-- Name: invoice_stock_logs_user_time_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoice_stock_logs_user_time_idx ON public.invoice_stock_logs USING btree (user_id, occurred_at DESC);


--
-- Name: invoices_receiver_address_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoices_receiver_address_idx ON public.invoices USING btree (receiver_address_id);


--
-- Name: invoices_sender_address_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoices_sender_address_idx ON public.invoices USING btree (sender_address_id);


--
-- Name: invoices_type_issue_date_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX invoices_type_issue_date_idx ON public.invoices USING btree (invoice_type, issue_date);


--
-- Name: ix_active_barcode_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_active_barcode_code ON public."qrActive" USING btree (barcode, qr_code);


--
-- Name: ix_branches_parent_branch_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_branches_parent_branch_id ON public.branches USING btree (parent_branch_id);


--
-- Name: ix_consignment_sale_items_sale; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_consignment_sale_items_sale ON public."consignmentSaleItems" USING btree (consignment_sale_id);


--
-- Name: ix_consignment_sale_payements_sale; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_consignment_sale_payements_sale ON public."consignmentSalePayements" USING btree (consignment_sale_id);


--
-- Name: ix_consignment_sales_patient_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_consignment_sales_patient_time ON public."consignmentSales" USING btree (patient_id, "timestamp" DESC);


--
-- Name: ix_consignment_sales_status_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_consignment_sales_status_time ON public."consignmentSales" USING btree (status, "timestamp" DESC);


--
-- Name: ix_depot_log_record_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_depot_log_record_time ON public.depot_log USING btree (record_key, "timestamp" DESC);


--
-- Name: ix_doctor_log_record_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_doctor_log_record_time ON public.doctor_log USING btree (record_key, "timestamp" DESC);


--
-- Name: ix_faculty_log_record_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_faculty_log_record_time ON public.faculty_log USING btree (record_key, "timestamp" DESC);


--
-- Name: ix_family_group_log_record_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_family_group_log_record_time ON public.family_group_log USING btree (record_key, "timestamp" DESC);


--
-- Name: ix_hanged_barcode_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_hanged_barcode_code ON public."qrHanged" USING btree (barcode, qr_code);


--
-- Name: ix_inactive_barcode_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_inactive_barcode_code ON public."qrInactive" USING btree (barcode, qr_code);


--
-- Name: ix_kurum_log_record_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_kurum_log_record_time ON public.kurum_log USING btree (record_key, "timestamp" DESC);


--
-- Name: ix_loyal_patient_medication_history_barcode; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_loyal_patient_medication_history_barcode ON public.loyal_patient_medication_history USING btree (medication_barcode);


--
-- Name: ix_loyal_patient_medication_history_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_loyal_patient_medication_history_patient ON public.loyal_patient_medication_history USING btree (patient_record_id, medication_purchase_date DESC);


--
-- Name: ix_loyal_patient_report_history_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_loyal_patient_report_history_patient ON public.loyal_patient_report_history USING btree (patient_record_id, start_date DESC);


--
-- Name: ix_loyal_report_evidence_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_loyal_report_evidence_patient ON public.loyal_patient_report_supply_evidence USING btree (patient_record_id, evaluated_at DESC);


--
-- Name: ix_patient_debt_log_patient_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_debt_log_patient_time ON public.patient_debt_log USING btree (patient_record_id, "timestamp" DESC);


--
-- Name: ix_patient_debt_log_reference; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_debt_log_reference ON public.patient_debt_log USING btree (reference_code);


--
-- Name: ix_patient_family_group_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_family_group_id ON public.patient USING btree (family_group_id);


--
-- Name: ix_patient_is_loyal; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_is_loyal ON public.patient USING btree (is_loyal) WHERE is_loyal;


--
-- Name: ix_patient_kurum_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_kurum_id ON public.patient USING btree (kurum_id);


--
-- Name: ix_patient_log_record_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_log_record_time ON public.patient_log USING btree (record_key, "timestamp" DESC);


--
-- Name: ix_patient_loyal_family_state; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_loyal_family_state ON public.patient USING btree (family_group_id, loyalty_mechanism3_state) WHERE (family_group_id IS NOT NULL);


--
-- Name: ix_patient_loyal_mechanism2; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_loyal_mechanism2 ON public.patient USING btree (is_loyal_mechanism2) WHERE is_loyal_mechanism2;


--
-- Name: ix_patient_loyal_mechanism3; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_loyal_mechanism3 ON public.patient USING btree (is_loyal_mechanism3) WHERE is_loyal_mechanism3;


--
-- Name: ix_patient_prescription_count; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_prescription_count ON public.patient USING btree (prescription_count DESC);


--
-- Name: ix_pharmacy_log_record_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_pharmacy_log_record_time ON public.pharmacy_log USING btree (record_key, "timestamp" DESC);


--
-- Name: ix_prescription_doctor_branch; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_doctor_branch ON public.prescription USING btree (doctor_branch_id);


--
-- Name: ix_prescription_doctor_registration; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_doctor_registration ON public.prescription USING btree (doctor_diploma_registration_no);


--
-- Name: ix_prescription_e_prescription_no; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_e_prescription_no ON public.prescription USING btree (e_prescription_no);


--
-- Name: ix_prescription_filter_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_filter_date ON public.prescription USING btree (medula_filter_date);


--
-- Name: ix_prescription_icd10_codes; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_icd10_codes ON public.prescription USING gin (icd10_diagnoses);


--
-- Name: ix_prescription_identity_fingerprint; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_identity_fingerprint ON public.prescription USING btree (identity_fingerprint);


--
-- Name: ix_prescription_invoice_type_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_invoice_type_code ON public.prescription USING btree (invoice_type_code);


--
-- Name: ix_prescription_log_action_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_log_action_time ON public.prescription_log USING btree (update_datetime DESC);


--
-- Name: ix_prescription_medula_prescription_no; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_medula_prescription_no ON public.prescription USING btree (medula_prescription_no);


--
-- Name: ix_prescription_patient_tc; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_patient_tc ON public.prescription USING btree (patient_tc_identity_no);


--
-- Name: ix_prescription_qr_status_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_qr_status_code ON public.prescription USING btree (qr_status_code);


--
-- Name: ix_prescription_sale_operations_uid_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_prescription_sale_operations_uid_time ON public.prescription_sale_operations USING btree (prescription_uid, prepared_at DESC);


--
-- Name: ix_qrHanged_barcode; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_qrHanged_barcode" ON public."qrHanged" USING btree (barcode);


--
-- Name: ix_qr_movements_code_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_qr_movements_code_date ON public."qrMovements" USING btree (qr_code, movement_date);


--
-- Name: ix_qr_movements_date_record; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_qr_movements_date_record ON public."qrMovements" USING btree (movement_date, record_id);


--
-- Name: ix_qractive_reserved_prescription; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_qractive_reserved_prescription ON public."qrActive" USING btree (reserved_prescription_uid) WHERE (reserved_prescription_uid IS NOT NULL);


--
-- Name: ix_qrmovements_reference_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_qrmovements_reference_code ON public."qrMovements" USING btree (reference_code);


--
-- Name: ix_retail_its_operations_refund; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_retail_its_operations_refund ON public.retail_its_operations USING btree (refund_id) WHERE (refund_id IS NOT NULL);


--
-- Name: ix_retail_its_operations_sale; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_retail_its_operations_sale ON public.retail_its_operations USING btree (sale_id) WHERE (sale_id IS NOT NULL);


--
-- Name: ix_retail_its_operations_status_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_retail_its_operations_status_time ON public.retail_its_operations USING btree (operation_status, prepared_at DESC);


--
-- Name: ix_sales_patient_timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sales_patient_timestamp ON public.sales USING btree (patient_id, "timestamp" DESC);


--
-- Name: ix_sales_prescription_no; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sales_prescription_no ON public.sales USING btree (prescription_no) WHERE (prescription_no IS NOT NULL);


--
-- Name: ix_sales_status_timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sales_status_timestamp ON public.sales USING btree (sale_status, "timestamp" DESC);


--
-- Name: ix_sales_timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sales_timestamp ON public.sales USING btree ("timestamp" DESC);


--
-- Name: ix_sales_user_timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sales_user_timestamp ON public.sales USING btree (user_no, "timestamp" DESC);


--
-- Name: ix_salespayements_sale_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_salespayements_sale_id ON public."salesPayements" USING btree (sale_id);


--
-- Name: ix_salespayements_sale_type; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_salespayements_sale_type ON public."salesPayements" USING btree (sale_id, payment_type);


--
-- Name: ix_salesqritems_qr_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_salesqritems_qr_code ON public."salesQrItems" USING btree (qr_code);


--
-- Name: ix_salesqritems_sale_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_salesqritems_sale_id ON public."salesQrItems" USING btree (sale_id);


--
-- Name: ix_salesrefunditems_qr_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_salesrefunditems_qr_code ON public."salesRefundItems" USING btree (qr_code);


--
-- Name: ix_salesrefunditems_refund_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_salesrefunditems_refund_id ON public."salesRefundItems" USING btree (refund_id);


--
-- Name: ix_salesrefundpayements_refund_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_salesrefundpayements_refund_id ON public."salesRefundPayements" USING btree (refund_id);


--
-- Name: ix_salesrefunds_historical_prescription_sale; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_salesrefunds_historical_prescription_sale ON public."salesRefunds" USING btree (original_sale_id, "timestamp") WHERE (((refund_source)::text = 'prescription'::text) AND historical_import);


--
-- Name: ix_salesrefunds_patient_timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_salesrefunds_patient_timestamp ON public."salesRefunds" USING btree (patient_id, "timestamp" DESC);


--
-- Name: ix_salesrefunds_prescription_uid; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_salesrefunds_prescription_uid ON public."salesRefunds" USING btree (prescription_uid) WHERE (prescription_uid IS NOT NULL);


--
-- Name: ix_stock_its_operations_status_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_stock_its_operations_status_time ON public.stock_its_operations USING btree (operation_status, prepared_at DESC);


--
-- Name: ix_takasBarcodeItems_barcode; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_takasBarcodeItems_barcode" ON public."takasBarcodeItems" USING btree (barcode);


--
-- Name: ix_takasBarcodeItems_takas_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_takasBarcodeItems_takas_status" ON public."takasBarcodeItems" USING btree (takas_id, status);


--
-- Name: ix_takasQrItems_qr_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_takasQrItems_qr_code" ON public."takasQrItems" USING btree (qr_code);


--
-- Name: ix_takasQrItems_takas_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_takasQrItems_takas_status" ON public."takasQrItems" USING btree (takas_id, status);


--
-- Name: ix_takas_pharmacy_created; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_takas_pharmacy_created ON public.takas USING btree (pharmacy_id, created_at DESC);


--
-- Name: ix_takas_status_next_check; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_takas_status_next_check ON public.takas USING btree (status, next_status_check_at) WHERE ((status)::text = ANY (ARRAY[('in_transit'::character varying)::text, ('partially_transferred'::character varying)::text, ('partially_accepted'::character varying)::text, ('reconciliation_required'::character varying)::text]));


--
-- Name: ix_unnotified_its_created; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_unnotified_its_created ON public.unnotified_its_movements USING btree (created_at DESC);


--
-- Name: ix_unnotified_its_reference; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_unnotified_its_reference ON public.unnotified_its_movements USING btree (reference_no);


--
-- Name: ix_users_log_record_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_users_log_record_time ON public.users_log USING btree (record_key, "timestamp" DESC);


--
-- Name: uq_takasQrItems_unresolved_qr; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "uq_takasQrItems_unresolved_qr" ON public."takasQrItems" USING btree (qr_code) WHERE ((status)::text = ANY (ARRAY[('pending_transfer'::character varying)::text, ('in_transit'::character varying)::text, ('reconciliation_required'::character varying)::text]));


--
-- Name: ux_barcode_analysis_default; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_barcode_analysis_default ON public."barcodeAnalysis" USING btree ((1)) WHERE (record_reference_no IS NULL);


--
-- Name: ux_barcode_analysis_product; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_barcode_analysis_product ON public."barcodeAnalysis" USING btree (record_reference_no) WHERE (record_reference_no IS NOT NULL);


--
-- Name: ux_consignment_sale_items_sale_qr; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_consignment_sale_items_sale_qr ON public."consignmentSaleItems" USING btree (consignment_sale_id, qr_code);


--
-- Name: ux_prescription_log_uid_version; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_prescription_log_uid_version ON public.prescription_log USING btree (prescription_uid, version_number);


--
-- Name: ux_prescription_sale_operations_active; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_prescription_sale_operations_active ON public.prescription_sale_operations USING btree (prescription_uid, operation_type) WHERE ((operation_status)::text = ANY (ARRAY[('prepared'::character varying)::text, ('remote_confirmed'::character varying)::text, ('reconciliation_required'::character varying)::text]));


--
-- Name: ux_prescription_uid_current; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_prescription_uid_current ON public.prescription USING btree (prescription_uid);


--
-- Name: ux_prescription_uid_version; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_prescription_uid_version ON public.prescription USING btree (prescription_uid, version_number);


--
-- Name: ux_retail_its_operation_items_active_qr; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_retail_its_operation_items_active_qr ON public.retail_its_operation_items USING btree (qr_code) WHERE is_active;


--
-- Name: ux_salesQrItems_sale_qr; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ux_salesQrItems_sale_qr" ON public."salesQrItems" USING btree (sale_id, qr_code) WHERE (((item_source)::text = 'prescription'::text) AND inventory_tracked);


--
-- Name: ux_salesRefundItems_refund_qr; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ux_salesRefundItems_refund_qr" ON public."salesRefundItems" USING btree (refund_id, qr_code) WHERE (((item_source)::text = 'prescription'::text) AND inventory_tracked);


--
-- Name: ux_sales_prescription_uid; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_sales_prescription_uid ON public.sales USING btree (prescription_uid) WHERE ((prescription_uid IS NOT NULL) AND ((sale_status)::text <> 'cancelled'::text));


--
-- Name: ux_salespayements_prescription_role; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_salespayements_prescription_role ON public."salesPayements" USING btree (sale_id, payment_role) WHERE ((payment_role)::text = ANY (ARRAY[('patient'::character varying)::text, ('institution_primary'::character varying)::text, ('institution_secondary'::character varying)::text]));


--
-- Name: ux_salesrefundpayements_prescription_role; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_salesrefundpayements_prescription_role ON public."salesRefundPayements" USING btree (refund_id, payment_role) WHERE ((payment_role)::text = 'patient'::text);


--
-- Name: ux_salesrefunds_prescription_sale; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_salesrefunds_prescription_sale ON public."salesRefunds" USING btree (original_sale_id) WHERE (((refund_source)::text = 'prescription'::text) AND (NOT historical_import));


--
-- Name: ux_stock_its_operation_items_active_qr; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_stock_its_operation_items_active_qr ON public.stock_its_operation_items USING btree (qr_code) WHERE is_active;


--
-- Name: ux_unnotified_its_open_qr; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_unnotified_its_open_qr ON public.unnotified_its_movements USING btree (qr_code) WHERE (NOT is_corrected);


--
-- Name: ux_users_tc_identity_no; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_users_tc_identity_no ON public.users USING btree (tc_identity_no);


--
-- Name: warning_icons_header_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX warning_icons_header_idx ON public."warningIcons" USING btree (warning_header);


--
-- Name: depot trg_depot_audit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_depot_audit AFTER INSERT OR DELETE OR UPDATE ON public.depot FOR EACH ROW EXECUTE FUNCTION public.audit_minor_card_change('depot_log', 'record_id');


--
-- Name: doctor trg_doctor_audit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_doctor_audit AFTER INSERT OR DELETE OR UPDATE ON public.doctor FOR EACH ROW EXECUTE FUNCTION public.audit_minor_card_change('doctor_log', 'record_id');


--
-- Name: faculty trg_faculty_audit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_faculty_audit AFTER INSERT OR DELETE OR UPDATE ON public.faculty FOR EACH ROW EXECUTE FUNCTION public.audit_minor_card_change('faculty_log', 'record_id');


--
-- Name: family_group trg_family_group_audit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_family_group_audit AFTER INSERT OR DELETE OR UPDATE ON public.family_group FOR EACH ROW EXECUTE FUNCTION public.audit_minor_card_change('family_group_log', 'record_id');


--
-- Name: family_group trg_family_group_validate_members; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_family_group_validate_members BEFORE INSERT OR UPDATE OF family_group_name, main_patient_id, member_ids ON public.family_group FOR EACH ROW EXECUTE FUNCTION public.validate_family_group_members();


--
-- Name: kurum trg_kurum_audit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_kurum_audit AFTER INSERT OR DELETE OR UPDATE ON public.kurum FOR EACH ROW EXECUTE FUNCTION public.audit_minor_card_change('kurum_log', 'record_id');


--
-- Name: patient trg_patient_audit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_patient_audit AFTER INSERT OR DELETE OR UPDATE ON public.patient FOR EACH ROW EXECUTE FUNCTION public.audit_minor_card_change('patient_log', 'record_id');


--
-- Name: patient_identity trg_patient_identity_prescription_count; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_patient_identity_prescription_count AFTER INSERT OR DELETE OR UPDATE OF tc_identity_no, patient_record_id ON public.patient_identity FOR EACH ROW EXECUTE FUNCTION public.refresh_patient_prescription_count_for_identity();


--
-- Name: patient trg_patient_refresh_family_group; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_patient_refresh_family_group AFTER INSERT OR DELETE OR UPDATE OF family_group_id ON public.patient FOR EACH ROW EXECUTE FUNCTION public.refresh_family_group_members();


--
-- Name: pharmacy trg_pharmacy_audit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_pharmacy_audit AFTER INSERT OR DELETE OR UPDATE ON public.pharmacy FOR EACH ROW EXECUTE FUNCTION public.audit_minor_card_change('pharmacy_log', 'record_id');


--
-- Name: prescription trg_prescription_icd10_codes; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_prescription_icd10_codes BEFORE INSERT OR UPDATE OF icd10_diagnoses ON public.prescription FOR EACH ROW EXECUTE FUNCTION public.validate_prescription_icd10_codes();


--
-- Name: prescription trg_prescription_patient_count; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_prescription_patient_count AFTER INSERT OR DELETE OR UPDATE OF patient_tc_identity_no, is_deleted ON public.prescription FOR EACH ROW EXECUTE FUNCTION public.track_patient_prescription_count();


--
-- Name: qrMovements trg_qrmovements_apply_consignment_return; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_qrmovements_apply_consignment_return BEFORE INSERT ON public."qrMovements" FOR EACH ROW EXECUTE FUNCTION public.pharmacy_apply_consignment_return();


--
-- Name: qrMovements trg_qrmovements_apply_stock; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_qrmovements_apply_stock AFTER INSERT ON public."qrMovements" FOR EACH ROW EXECUTE FUNCTION public.pharmacy_apply_qr_movement();


--
-- Name: salesPayements trg_salespayements_refresh_sale; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_salespayements_refresh_sale AFTER INSERT OR DELETE OR UPDATE ON public."salesPayements" FOR EACH ROW EXECUTE FUNCTION public.pharmacy_apply_sale_payment_status();


--
-- Name: salesRefundPayements trg_salesrefundpayements_refresh_refund; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_salesrefundpayements_refresh_refund AFTER INSERT OR DELETE OR UPDATE ON public."salesRefundPayements" FOR EACH ROW EXECUTE FUNCTION public.pharmacy_apply_refund_payment_status();


--
-- Name: users trg_users_audit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_users_audit AFTER INSERT OR DELETE OR UPDATE ON public.users FOR EACH ROW EXECUTE FUNCTION public.audit_minor_card_change('users_log', 'user_id');


--
-- Name: barcodeAnalysis barcodeAnalysis_record_reference_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."barcodeAnalysis"
    ADD CONSTRAINT "barcodeAnalysis_record_reference_no_fkey" FOREIGN KEY (record_reference_no) REFERENCES public.barcode(record_id) ON DELETE CASCADE;


--
-- Name: branches branches_parent_branch_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.branches
    ADD CONSTRAINT branches_parent_branch_id_fkey FOREIGN KEY (parent_branch_id) REFERENCES public.branches(branch_id);


--
-- Name: consignmentSaleItems consignmentSaleItems_consignment_sale_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."consignmentSaleItems"
    ADD CONSTRAINT "consignmentSaleItems_consignment_sale_id_fkey" FOREIGN KEY (consignment_sale_id) REFERENCES public."consignmentSales"(record_id) ON DELETE CASCADE;


--
-- Name: consignmentSalePayements consignmentSalePayements_consignment_sale_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."consignmentSalePayements"
    ADD CONSTRAINT "consignmentSalePayements_consignment_sale_id_fkey" FOREIGN KEY (consignment_sale_id) REFERENCES public."consignmentSales"(record_id) ON DELETE CASCADE;


--
-- Name: consignmentSalePayements consignmentSalePayements_payment_type_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."consignmentSalePayements"
    ADD CONSTRAINT "consignmentSalePayements_payment_type_fkey" FOREIGN KEY (payment_type) REFERENCES public."payementTypes"(record_id);


--
-- Name: consignmentSales consignmentSales_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."consignmentSales"
    ADD CONSTRAINT "consignmentSales_patient_id_fkey" FOREIGN KEY (patient_id) REFERENCES public.patient(record_id);


--
-- Name: consignmentSales consignmentSales_settled_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."consignmentSales"
    ADD CONSTRAINT "consignmentSales_settled_by_fkey" FOREIGN KEY (settled_by) REFERENCES public.users(user_id);


--
-- Name: consignmentSales consignmentSales_settlement_sale_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."consignmentSales"
    ADD CONSTRAINT "consignmentSales_settlement_sale_id_fkey" FOREIGN KEY (settlement_sale_id) REFERENCES public.sales(record_id);


--
-- Name: consignmentSales consignmentSales_user_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."consignmentSales"
    ADD CONSTRAINT "consignmentSales_user_no_fkey" FOREIGN KEY (user_no) REFERENCES public.users(user_id);


--
-- Name: doctor doctor_branch_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.doctor
    ADD CONSTRAINT doctor_branch_id_fkey FOREIGN KEY (branch_id) REFERENCES public.branches(branch_id);


--
-- Name: doctor doctor_facility_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.doctor
    ADD CONSTRAINT doctor_facility_no_fkey FOREIGN KEY (facility_no) REFERENCES public.faculty(faculty_code);


--
-- Name: faculty faculty_faculty_type_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.faculty
    ADD CONSTRAINT faculty_faculty_type_id_fkey FOREIGN KEY (faculty_type_id) REFERENCES public.faculty_type(faculty_type_id);


--
-- Name: family_group family_group_main_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.family_group
    ADD CONSTRAINT family_group_main_patient_id_fkey FOREIGN KEY (main_patient_id) REFERENCES public.patient(record_id) ON DELETE SET NULL;


--
-- Name: barcodeCampaign farmassist_barcodeCampaign_record_fk; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."barcodeCampaign"
    ADD CONSTRAINT "farmassist_barcodeCampaign_record_fk" FOREIGN KEY (record_reference_no) REFERENCES public.barcode(record_id) ON DELETE CASCADE;


--
-- Name: barcodePriceHistory farmassist_barcodePriceHistory_record_fk; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."barcodePriceHistory"
    ADD CONSTRAINT "farmassist_barcodePriceHistory_record_fk" FOREIGN KEY (record_reference_no) REFERENCES public.barcode(record_id) ON DELETE CASCADE;


--
-- Name: barcodePrices farmassist_barcodePrices_record_fk; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."barcodePrices"
    ADD CONSTRAINT "farmassist_barcodePrices_record_fk" FOREIGN KEY (record_reference_no) REFERENCES public.barcode(record_id) ON DELETE CASCADE;


--
-- Name: qrMovements fk_qrMovements_movement_type; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."qrMovements"
    ADD CONSTRAINT "fk_qrMovements_movement_type" FOREIGN KEY (movement_type) REFERENCES public."qrMovementTypes"(record_id);


--
-- Name: invoice_line_qr_codes invoice_line_qr_codes_invoice_line_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_line_qr_codes
    ADD CONSTRAINT invoice_line_qr_codes_invoice_line_id_fkey FOREIGN KEY (invoice_line_id) REFERENCES public.invoice_lines(record_id) ON DELETE CASCADE;


--
-- Name: invoice_lines invoice_lines_compact_invoice_uuid_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_lines
    ADD CONSTRAINT invoice_lines_compact_invoice_uuid_fkey FOREIGN KEY (invoice_uuid) REFERENCES public.invoices(invoice_uuid) ON DELETE CASCADE;


--
-- Name: invoice_stock_log_invoices invoice_stock_log_invoices_invoice_stock_log_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_stock_log_invoices
    ADD CONSTRAINT invoice_stock_log_invoices_invoice_stock_log_id_fkey FOREIGN KEY (invoice_stock_log_id) REFERENCES public.invoice_stock_logs(record_id) ON DELETE CASCADE;


--
-- Name: invoice_stock_log_invoices invoice_stock_log_invoices_invoice_uuid_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_stock_log_invoices
    ADD CONSTRAINT invoice_stock_log_invoices_invoice_uuid_fkey FOREIGN KEY (invoice_uuid) REFERENCES public.invoices(invoice_uuid);


--
-- Name: invoice_stock_logs invoice_stock_logs_invoice_uuid_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_stock_logs
    ADD CONSTRAINT invoice_stock_logs_invoice_uuid_fkey FOREIGN KEY (invoice_uuid) REFERENCES public.invoices(invoice_uuid);


--
-- Name: invoice_stock_logs invoice_stock_logs_movement_type_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_stock_logs
    ADD CONSTRAINT invoice_stock_logs_movement_type_id_fkey FOREIGN KEY (movement_type_id) REFERENCES public."qrMovementTypes"(record_id);


--
-- Name: invoice_stock_logs invoice_stock_logs_related_log_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_stock_logs
    ADD CONSTRAINT invoice_stock_logs_related_log_id_fkey FOREIGN KEY (related_log_id) REFERENCES public.invoice_stock_logs(record_id);


--
-- Name: invoice_stock_logs invoice_stock_logs_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoice_stock_logs
    ADD CONSTRAINT invoice_stock_logs_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(user_id);


--
-- Name: invoices invoices_compact_receiver_address_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoices
    ADD CONSTRAINT invoices_compact_receiver_address_id_fkey FOREIGN KEY (receiver_address_id) REFERENCES public.addresses(record_id);


--
-- Name: invoices invoices_compact_sender_address_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.invoices
    ADD CONSTRAINT invoices_compact_sender_address_id_fkey FOREIGN KEY (sender_address_id) REFERENCES public.addresses(record_id);


--
-- Name: loyal_patient_benchmark_cohort loyal_patient_benchmark_cohort_patient_record_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_benchmark_cohort
    ADD CONSTRAINT loyal_patient_benchmark_cohort_patient_record_id_fkey FOREIGN KEY (patient_record_id) REFERENCES public.patient(record_id) ON DELETE CASCADE;


--
-- Name: loyal_patient_medication_history loyal_patient_medication_history_patient_record_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_medication_history
    ADD CONSTRAINT loyal_patient_medication_history_patient_record_id_fkey FOREIGN KEY (patient_record_id) REFERENCES public.loyal_patient_profile(patient_record_id) ON DELETE CASCADE;


--
-- Name: loyal_patient_profile loyal_patient_profile_patient_record_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_profile
    ADD CONSTRAINT loyal_patient_profile_patient_record_id_fkey FOREIGN KEY (patient_record_id) REFERENCES public.patient(record_id) ON DELETE CASCADE;


--
-- Name: loyal_patient_report_history loyal_patient_report_history_patient_record_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_report_history
    ADD CONSTRAINT loyal_patient_report_history_patient_record_id_fkey FOREIGN KEY (patient_record_id) REFERENCES public.loyal_patient_profile(patient_record_id) ON DELETE CASCADE;


--
-- Name: loyal_patient_report_supply_evidence loyal_patient_report_supply_evidence_patient_record_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_report_supply_evidence
    ADD CONSTRAINT loyal_patient_report_supply_evidence_patient_record_id_fkey FOREIGN KEY (patient_record_id) REFERENCES public.patient(record_id) ON DELETE CASCADE;


--
-- Name: loyal_patient_report_supply_evidence loyal_patient_report_supply_evidence_run_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.loyal_patient_report_supply_evidence
    ADD CONSTRAINT loyal_patient_report_supply_evidence_run_id_fkey FOREIGN KEY (run_id) REFERENCES public.loyal_patient_report_sample_run(run_id) ON DELETE CASCADE;


--
-- Name: manual_movements manual_movements_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.manual_movements
    ADD CONSTRAINT manual_movements_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(user_id);


--
-- Name: patient patient_family_doctor_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient
    ADD CONSTRAINT patient_family_doctor_id_fkey FOREIGN KEY (family_doctor_id) REFERENCES public.doctor(diploma_registration_no);


--
-- Name: patient patient_family_group_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient
    ADD CONSTRAINT patient_family_group_id_fkey FOREIGN KEY (family_group_id) REFERENCES public.family_group(record_id) ON DELETE SET NULL;


--
-- Name: patient_identity patient_identity_patient_record_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient_identity
    ADD CONSTRAINT patient_identity_patient_record_id_fkey FOREIGN KEY (patient_record_id) REFERENCES public.patient(record_id);


--
-- Name: patient patient_kurum_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient
    ADD CONSTRAINT patient_kurum_id_fkey FOREIGN KEY (kurum_id) REFERENCES public.kurum(record_id);


--
-- Name: prescription prescription_doctor_branch_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription
    ADD CONSTRAINT prescription_doctor_branch_id_fkey FOREIGN KEY (doctor_branch_id) REFERENCES public.branches(branch_id);


--
-- Name: prescription prescription_doctor_diploma_registration_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription
    ADD CONSTRAINT prescription_doctor_diploma_registration_no_fkey FOREIGN KEY (doctor_diploma_registration_no) REFERENCES public.doctor(diploma_registration_no);


--
-- Name: prescription prescription_doctor_registration_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription
    ADD CONSTRAINT prescription_doctor_registration_no_fkey FOREIGN KEY (doctor_diploma_registration_no) REFERENCES public.doctor(diploma_registration_no);


--
-- Name: prescription prescription_facility_code_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription
    ADD CONSTRAINT prescription_facility_code_fkey FOREIGN KEY (facility_code) REFERENCES public.faculty(faculty_code);


--
-- Name: prescription prescription_invoice_type_code_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription
    ADD CONSTRAINT prescription_invoice_type_code_fkey FOREIGN KEY (invoice_type_code) REFERENCES public.prescription_invoice_type(invoice_type_code);


--
-- Name: prescription prescription_patient_tc_identity_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription
    ADD CONSTRAINT prescription_patient_tc_identity_no_fkey FOREIGN KEY (patient_tc_identity_no) REFERENCES public.patient_identity(tc_identity_no);


--
-- Name: prescription prescription_recipient_tc_identity_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription
    ADD CONSTRAINT prescription_recipient_tc_identity_no_fkey FOREIGN KEY (recipient_tc_identity_no) REFERENCES public.patient_identity(tc_identity_no);


--
-- Name: prescription_sale_operations prescription_sale_operations_requested_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription_sale_operations
    ADD CONSTRAINT prescription_sale_operations_requested_by_fkey FOREIGN KEY (requested_by) REFERENCES public.users(user_id);


--
-- Name: prescription_sale_operations prescription_sale_operations_sale_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.prescription_sale_operations
    ADD CONSTRAINT prescription_sale_operations_sale_id_fkey FOREIGN KEY (sale_id) REFERENCES public.sales(record_id);


--
-- Name: qrHanged qrHanged_takas_item_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."qrHanged"
    ADD CONSTRAINT "qrHanged_takas_item_id_fkey" FOREIGN KEY (takas_item_id) REFERENCES public."takasQrItems"(record_id);


--
-- Name: retail_its_operation_items retail_its_operation_items_operation_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retail_its_operation_items
    ADD CONSTRAINT retail_its_operation_items_operation_id_fkey FOREIGN KEY (operation_id) REFERENCES public.retail_its_operations(operation_id) ON DELETE CASCADE;


--
-- Name: retail_its_operations retail_its_operations_refund_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retail_its_operations
    ADD CONSTRAINT retail_its_operations_refund_id_fkey FOREIGN KEY (refund_id) REFERENCES public."salesRefunds"(record_id);


--
-- Name: retail_its_operations retail_its_operations_sale_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retail_its_operations
    ADD CONSTRAINT retail_its_operations_sale_id_fkey FOREIGN KEY (sale_id) REFERENCES public.sales(record_id);


--
-- Name: salesPayements salesPayements_payment_type_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesPayements"
    ADD CONSTRAINT "salesPayements_payment_type_fkey" FOREIGN KEY (payment_type) REFERENCES public."payementTypes"(record_id);


--
-- Name: salesPayements salesPayements_sale_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesPayements"
    ADD CONSTRAINT "salesPayements_sale_id_fkey" FOREIGN KEY (sale_id) REFERENCES public.sales(record_id) ON DELETE CASCADE;


--
-- Name: salesQrItems salesQrItems_sale_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesQrItems"
    ADD CONSTRAINT "salesQrItems_sale_id_fkey" FOREIGN KEY (sale_id) REFERENCES public.sales(record_id) ON DELETE CASCADE;


--
-- Name: salesRefundItems salesRefundItems_original_sale_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesRefundItems"
    ADD CONSTRAINT "salesRefundItems_original_sale_id_fkey" FOREIGN KEY (original_sale_id) REFERENCES public.sales(record_id);


--
-- Name: salesRefundItems salesRefundItems_refund_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesRefundItems"
    ADD CONSTRAINT "salesRefundItems_refund_id_fkey" FOREIGN KEY (refund_id) REFERENCES public."salesRefunds"(record_id) ON DELETE CASCADE;


--
-- Name: salesRefundPayements salesRefundPayements_payment_type_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesRefundPayements"
    ADD CONSTRAINT "salesRefundPayements_payment_type_fkey" FOREIGN KEY (payment_type) REFERENCES public."payementTypes"(record_id);


--
-- Name: salesRefundPayements salesRefundPayements_refund_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesRefundPayements"
    ADD CONSTRAINT "salesRefundPayements_refund_id_fkey" FOREIGN KEY (refund_id) REFERENCES public."salesRefunds"(record_id) ON DELETE CASCADE;


--
-- Name: salesRefunds salesRefunds_original_sale_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesRefunds"
    ADD CONSTRAINT "salesRefunds_original_sale_id_fkey" FOREIGN KEY (original_sale_id) REFERENCES public.sales(record_id);


--
-- Name: salesRefunds salesRefunds_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesRefunds"
    ADD CONSTRAINT "salesRefunds_patient_id_fkey" FOREIGN KEY (patient_id) REFERENCES public.patient(record_id);


--
-- Name: salesRefunds salesRefunds_user_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."salesRefunds"
    ADD CONSTRAINT "salesRefunds_user_no_fkey" FOREIGN KEY (user_no) REFERENCES public.users(user_id);


--
-- Name: sales sales_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales
    ADD CONSTRAINT sales_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(record_id);


--
-- Name: sales sales_user_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales
    ADD CONSTRAINT sales_user_no_fkey FOREIGN KEY (user_no) REFERENCES public.users(user_id);


--
-- Name: scheduled_stock_movements scheduled_stock_movements_record_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.scheduled_stock_movements
    ADD CONSTRAINT scheduled_stock_movements_record_id_fkey FOREIGN KEY (record_id) REFERENCES public."qrMovements"(record_id) ON DELETE CASCADE;


--
-- Name: stock_its_operation_items stock_its_operation_items_operation_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.stock_its_operation_items
    ADD CONSTRAINT stock_its_operation_items_operation_id_fkey FOREIGN KEY (operation_id) REFERENCES public.stock_its_operations(operation_id) ON DELETE CASCADE;


--
-- Name: stock_its_operations stock_its_operations_manual_movement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.stock_its_operations
    ADD CONSTRAINT stock_its_operations_manual_movement_id_fkey FOREIGN KEY (manual_movement_id) REFERENCES public.manual_movements(record_id);


--
-- Name: stock_its_operations stock_its_operations_requested_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.stock_its_operations
    ADD CONSTRAINT stock_its_operations_requested_by_fkey FOREIGN KEY (requested_by) REFERENCES public.users(user_id);


--
-- Name: takasBarcodeItems takasBarcodeItems_takas_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."takasBarcodeItems"
    ADD CONSTRAINT "takasBarcodeItems_takas_id_fkey" FOREIGN KEY (takas_id) REFERENCES public.takas(record_id) ON DELETE CASCADE;


--
-- Name: takasQrItems takasQrItems_takas_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."takasQrItems"
    ADD CONSTRAINT "takasQrItems_takas_id_fkey" FOREIGN KEY (takas_id) REFERENCES public.takas(record_id) ON DELETE CASCADE;


--
-- Name: takas takas_pharmacy_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.takas
    ADD CONSTRAINT takas_pharmacy_id_fkey FOREIGN KEY (pharmacy_id) REFERENCES public.pharmacy(record_id);


--
-- Name: takas takas_user_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.takas
    ADD CONSTRAINT takas_user_no_fkey FOREIGN KEY (user_no) REFERENCES public.users(user_id);


--
-- Name: unnotified_its_movements unnotified_its_movements_corrected_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unnotified_its_movements
    ADD CONSTRAINT unnotified_its_movements_corrected_by_fkey FOREIGN KEY (corrected_by) REFERENCES public.users(user_id);


--
-- Name: unnotified_its_movements unnotified_its_movements_created_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unnotified_its_movements
    ADD CONSTRAINT unnotified_its_movements_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.users(user_id);


--
-- Name: unnotified_its_movements unnotified_its_movements_paired_record_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unnotified_its_movements
    ADD CONSTRAINT unnotified_its_movements_paired_record_id_fkey FOREIGN KEY (paired_record_id) REFERENCES public.unnotified_its_movements(record_id);


--
-- Name: barcodeAnalysis; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."barcodeAnalysis" ENABLE ROW LEVEL SECURITY;

--
-- Name: barcodeCampaign; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."barcodeCampaign" ENABLE ROW LEVEL SECURITY;

--
-- Name: barcodePriceHistory; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."barcodePriceHistory" ENABLE ROW LEVEL SECURITY;

--
-- Name: consignmentSaleItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."consignmentSaleItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: consignmentSalePayements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."consignmentSalePayements" ENABLE ROW LEVEL SECURITY;

--
-- Name: consignmentSales; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."consignmentSales" ENABLE ROW LEVEL SECURITY;

--
-- Name: barcodeAnalysis current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."barcodeAnalysis" USING (((updated_at IS NULL) OR (updated_at <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text)))) WITH CHECK (((updated_at IS NULL) OR (updated_at <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))));


--
-- Name: barcodeCampaign current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."barcodeCampaign" USING (((start_date IS NULL) OR (start_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date))) WITH CHECK (((start_date IS NULL) OR (start_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)));


--
-- Name: barcodePriceHistory current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."barcodePriceHistory" USING (((effective_date IS NULL) OR (effective_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date))) WITH CHECK (((effective_date IS NULL) OR (effective_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)));


--
-- Name: consignmentSaleItems current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."consignmentSaleItems" USING (((consignment_sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."consignmentSales" parent
  WHERE (parent.record_id = "consignmentSaleItems".consignment_sale_id))))) WITH CHECK (((consignment_sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."consignmentSales" parent
  WHERE (parent.record_id = "consignmentSaleItems".consignment_sale_id)))));


--
-- Name: consignmentSalePayements current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."consignmentSalePayements" USING (((consignment_sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."consignmentSales" parent
  WHERE (parent.record_id = "consignmentSalePayements".consignment_sale_id))))) WITH CHECK (((consignment_sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."consignmentSales" parent
  WHERE (parent.record_id = "consignmentSalePayements".consignment_sale_id)))));


--
-- Name: consignmentSales current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."consignmentSales" USING (((("timestamp" IS NULL) OR ("timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))) AND ((settlement_sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = "consignmentSales".settlement_sale_id)))))) WITH CHECK (((("timestamp" IS NULL) OR ("timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))) AND ((settlement_sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = "consignmentSales".settlement_sale_id))))));


--
-- Name: depot_log current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.depot_log USING ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp()))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp())));


--
-- Name: doctor_log current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.doctor_log USING ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp()))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp())));


--
-- Name: faculty_log current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.faculty_log USING ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp()))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp())));


--
-- Name: family_group_log current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.family_group_log USING ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp()))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp())));


--
-- Name: invoice_incremental_sync_items current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.invoice_incremental_sync_items USING ((((received_at IS NULL) OR (received_at <= clock_timestamp())) AND ((issue_date IS NULL) OR (issue_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)))) WITH CHECK ((((received_at IS NULL) OR (received_at <= clock_timestamp())) AND ((issue_date IS NULL) OR (issue_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date))));


--
-- Name: invoice_incremental_sync_state current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.invoice_incremental_sync_state USING (((updated_at IS NULL) OR (updated_at <= clock_timestamp()))) WITH CHECK (((updated_at IS NULL) OR (updated_at <= clock_timestamp())));


--
-- Name: invoice_line_qr_codes current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.invoice_line_qr_codes USING (((invoice_line_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.invoice_lines parent
  WHERE (parent.record_id = invoice_line_qr_codes.invoice_line_id))))) WITH CHECK (((invoice_line_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.invoice_lines parent
  WHERE (parent.record_id = invoice_line_qr_codes.invoice_line_id)))));


--
-- Name: invoice_lines current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.invoice_lines USING (((invoice_uuid IS NULL) OR (EXISTS ( SELECT 1
   FROM public.invoices parent
  WHERE (parent.invoice_uuid = invoice_lines.invoice_uuid))))) WITH CHECK (((invoice_uuid IS NULL) OR (EXISTS ( SELECT 1
   FROM public.invoices parent
  WHERE (parent.invoice_uuid = invoice_lines.invoice_uuid)))));


--
-- Name: invoice_stock_log_invoices current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.invoice_stock_log_invoices USING ((((invoice_stock_log_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.invoice_stock_logs parent
  WHERE (parent.record_id = invoice_stock_log_invoices.invoice_stock_log_id)))) AND ((invoice_uuid IS NULL) OR (EXISTS ( SELECT 1
   FROM public.invoices parent
  WHERE (parent.invoice_uuid = invoice_stock_log_invoices.invoice_uuid)))))) WITH CHECK ((((invoice_stock_log_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.invoice_stock_logs parent
  WHERE (parent.record_id = invoice_stock_log_invoices.invoice_stock_log_id)))) AND ((invoice_uuid IS NULL) OR (EXISTS ( SELECT 1
   FROM public.invoices parent
  WHERE (parent.invoice_uuid = invoice_stock_log_invoices.invoice_uuid))))));


--
-- Name: invoice_stock_logs current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.invoice_stock_logs USING ((((occurred_at IS NULL) OR (occurred_at <= clock_timestamp())) AND ((invoice_uuid IS NULL) OR (EXISTS ( SELECT 1
   FROM public.invoices parent
  WHERE (parent.invoice_uuid = invoice_stock_logs.invoice_uuid)))))) WITH CHECK ((((occurred_at IS NULL) OR (occurred_at <= clock_timestamp())) AND ((invoice_uuid IS NULL) OR (EXISTS ( SELECT 1
   FROM public.invoices parent
  WHERE (parent.invoice_uuid = invoice_stock_logs.invoice_uuid))))));


--
-- Name: invoices current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.invoices USING ((((database_entry_at IS NULL) OR (database_entry_at <= clock_timestamp())) AND ((issue_date IS NULL) OR (issue_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)))) WITH CHECK ((((database_entry_at IS NULL) OR (database_entry_at <= clock_timestamp())) AND ((issue_date IS NULL) OR (issue_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date))));


--
-- Name: kurum_log current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.kurum_log USING ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp()))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp())));


--
-- Name: loyal_patient_benchmark_cohort current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.loyal_patient_benchmark_cohort USING (((selected_at IS NULL) OR (selected_at <= clock_timestamp()))) WITH CHECK (((selected_at IS NULL) OR (selected_at <= clock_timestamp())));


--
-- Name: loyal_patient_medication_history current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.loyal_patient_medication_history USING ((((medication_purchase_date IS NULL) OR (medication_purchase_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)) AND ((synced_at IS NULL) OR (synced_at <= clock_timestamp())) AND ((patient_record_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.loyal_patient_profile parent
  WHERE (parent.patient_record_id = loyal_patient_medication_history.patient_record_id)))))) WITH CHECK ((((medication_purchase_date IS NULL) OR (medication_purchase_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)) AND ((synced_at IS NULL) OR (synced_at <= clock_timestamp())) AND ((patient_record_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.loyal_patient_profile parent
  WHERE (parent.patient_record_id = loyal_patient_medication_history.patient_record_id))))));


--
-- Name: loyal_patient_profile current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.loyal_patient_profile USING ((((medication_history_synced_at IS NULL) OR (medication_history_synced_at <= clock_timestamp())) AND ((report_history_synced_at IS NULL) OR (report_history_synced_at <= clock_timestamp())))) WITH CHECK ((((medication_history_synced_at IS NULL) OR (medication_history_synced_at <= clock_timestamp())) AND ((report_history_synced_at IS NULL) OR (report_history_synced_at <= clock_timestamp()))));


--
-- Name: loyal_patient_report_history current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.loyal_patient_report_history USING ((((start_date IS NULL) OR (start_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)) AND ((synced_at IS NULL) OR (synced_at <= clock_timestamp())) AND ((patient_record_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.loyal_patient_profile parent
  WHERE (parent.patient_record_id = loyal_patient_report_history.patient_record_id)))))) WITH CHECK ((((start_date IS NULL) OR (start_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)) AND ((synced_at IS NULL) OR (synced_at <= clock_timestamp())) AND ((patient_record_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.loyal_patient_profile parent
  WHERE (parent.patient_record_id = loyal_patient_report_history.patient_record_id))))));


--
-- Name: loyal_patient_report_sample_run current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.loyal_patient_report_sample_run USING (((started_at IS NULL) OR (started_at <= clock_timestamp()))) WITH CHECK (((started_at IS NULL) OR (started_at <= clock_timestamp())));


--
-- Name: loyal_patient_report_supply_evidence current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.loyal_patient_report_supply_evidence USING ((((evaluated_at IS NULL) OR (evaluated_at <= clock_timestamp())) AND ((run_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.loyal_patient_report_sample_run parent
  WHERE (parent.run_id = loyal_patient_report_supply_evidence.run_id)))))) WITH CHECK ((((evaluated_at IS NULL) OR (evaluated_at <= clock_timestamp())) AND ((run_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.loyal_patient_report_sample_run parent
  WHERE (parent.run_id = loyal_patient_report_supply_evidence.run_id))))));


--
-- Name: manual_movements current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.manual_movements USING ((("timestamp" IS NULL) OR ("timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text)))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))));


--
-- Name: patient_debt_log current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.patient_debt_log USING ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp()))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp())));


--
-- Name: patient_log current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.patient_log USING ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp()))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp())));


--
-- Name: pharmacy_log current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.pharmacy_log USING ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp()))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp())));


--
-- Name: prescription current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.prescription USING ((((saved_at IS NULL) OR (saved_at <= clock_timestamp())) AND ((prescription_date IS NULL) OR (prescription_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)) AND ((medication_purchase_date IS NULL) OR (medication_purchase_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)) AND ((medula_filter_date IS NULL) OR (medula_filter_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)))) WITH CHECK ((((saved_at IS NULL) OR (saved_at <= clock_timestamp())) AND ((prescription_date IS NULL) OR (prescription_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)) AND ((medication_purchase_date IS NULL) OR (medication_purchase_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)) AND ((medula_filter_date IS NULL) OR (medula_filter_date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date))));


--
-- Name: prescription_log current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.prescription_log USING ((((update_datetime IS NULL) OR (update_datetime <= clock_timestamp())) AND ((NULLIF((prescription_snapshot ->> 'saved_at'::text), ''::text) IS NULL) OR (((prescription_snapshot ->> 'saved_at'::text))::timestamp with time zone <= clock_timestamp())) AND ((NULLIF((prescription_snapshot ->> 'prescription_date'::text), ''::text) IS NULL) OR (((prescription_snapshot ->> 'prescription_date'::text))::date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date)))) WITH CHECK ((((update_datetime IS NULL) OR (update_datetime <= clock_timestamp())) AND ((NULLIF((prescription_snapshot ->> 'saved_at'::text), ''::text) IS NULL) OR (((prescription_snapshot ->> 'saved_at'::text))::timestamp with time zone <= clock_timestamp())) AND ((NULLIF((prescription_snapshot ->> 'prescription_date'::text), ''::text) IS NULL) OR (((prescription_snapshot ->> 'prescription_date'::text))::date <= ((clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))::date))));


--
-- Name: prescription_sale_operations current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.prescription_sale_operations USING ((((prepared_at IS NULL) OR (prepared_at <= clock_timestamp())) AND ((sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = prescription_sale_operations.sale_id)))))) WITH CHECK ((((prepared_at IS NULL) OR (prepared_at <= clock_timestamp())) AND ((sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = prescription_sale_operations.sale_id))))));


--
-- Name: qrActive current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."qrActive" USING ((EXISTS ( SELECT 1
   FROM public."qrMovements" movement
  WHERE ((movement.qr_code)::text = ("qrActive".qr_code)::text)))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."qrMovements" movement
  WHERE ((movement.qr_code)::text = ("qrActive".qr_code)::text))));


--
-- Name: qrHanged current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."qrHanged" USING ((((hanged_at IS NULL) OR (hanged_at <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))) AND ((takas_item_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."takasQrItems" parent
  WHERE (parent.record_id = "qrHanged".takas_item_id)))))) WITH CHECK ((((hanged_at IS NULL) OR (hanged_at <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))) AND ((takas_item_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."takasQrItems" parent
  WHERE (parent.record_id = "qrHanged".takas_item_id))))));


--
-- Name: qrInactive current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."qrInactive" USING ((EXISTS ( SELECT 1
   FROM public."qrMovements" movement
  WHERE ((movement.qr_code)::text = ("qrInactive".qr_code)::text)))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."qrMovements" movement
  WHERE ((movement.qr_code)::text = ("qrInactive".qr_code)::text))));


--
-- Name: qrMovements current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."qrMovements" USING (((movement_date IS NULL) OR (movement_date <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text)))) WITH CHECK (((movement_date IS NULL) OR (movement_date <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))));


--
-- Name: retail_its_operation_items current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.retail_its_operation_items USING (((operation_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.retail_its_operations parent
  WHERE (parent.operation_id = retail_its_operation_items.operation_id))))) WITH CHECK (((operation_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.retail_its_operations parent
  WHERE (parent.operation_id = retail_its_operation_items.operation_id)))));


--
-- Name: retail_its_operations current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.retail_its_operations USING ((((prepared_at IS NULL) OR (prepared_at <= clock_timestamp())) AND ((refund_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."salesRefunds" parent
  WHERE (parent.record_id = retail_its_operations.refund_id)))) AND ((sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = retail_its_operations.sale_id)))))) WITH CHECK ((((prepared_at IS NULL) OR (prepared_at <= clock_timestamp())) AND ((refund_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."salesRefunds" parent
  WHERE (parent.record_id = retail_its_operations.refund_id)))) AND ((sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = retail_its_operations.sale_id))))));


--
-- Name: sales current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.sales USING ((("timestamp" IS NULL) OR ("timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text)))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))));


--
-- Name: salesPayements current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."salesPayements" USING (((sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = "salesPayements".sale_id))))) WITH CHECK (((sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = "salesPayements".sale_id)))));


--
-- Name: salesQrItems current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."salesQrItems" USING (((sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = "salesQrItems".sale_id))))) WITH CHECK (((sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = "salesQrItems".sale_id)))));


--
-- Name: salesRefundItems current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."salesRefundItems" USING ((((original_sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = "salesRefundItems".original_sale_id)))) AND ((refund_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."salesRefunds" parent
  WHERE (parent.record_id = "salesRefundItems".refund_id)))))) WITH CHECK ((((original_sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = "salesRefundItems".original_sale_id)))) AND ((refund_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."salesRefunds" parent
  WHERE (parent.record_id = "salesRefundItems".refund_id))))));


--
-- Name: salesRefundPayements current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."salesRefundPayements" USING (((refund_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."salesRefunds" parent
  WHERE (parent.record_id = "salesRefundPayements".refund_id))))) WITH CHECK (((refund_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public."salesRefunds" parent
  WHERE (parent.record_id = "salesRefundPayements".refund_id)))));


--
-- Name: salesRefunds current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."salesRefunds" USING (((("timestamp" IS NULL) OR ("timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))) AND ((original_sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = "salesRefunds".original_sale_id)))))) WITH CHECK (((("timestamp" IS NULL) OR ("timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))) AND ((original_sale_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.sales parent
  WHERE (parent.record_id = "salesRefunds".original_sale_id))))));


--
-- Name: scheduled_stock_movements current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.scheduled_stock_movements USING ((EXISTS ( SELECT 1
   FROM public."qrMovements" movement
  WHERE (movement.record_id = scheduled_stock_movements.record_id)))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."qrMovements" movement
  WHERE (movement.record_id = scheduled_stock_movements.record_id))));


--
-- Name: stock_its_operation_items current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.stock_its_operation_items USING (((operation_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.stock_its_operations parent
  WHERE (parent.operation_id = stock_its_operation_items.operation_id))))) WITH CHECK (((operation_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.stock_its_operations parent
  WHERE (parent.operation_id = stock_its_operation_items.operation_id)))));


--
-- Name: stock_its_operations current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.stock_its_operations USING ((((prepared_at IS NULL) OR (prepared_at <= clock_timestamp())) AND ((manual_movement_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.manual_movements parent
  WHERE (parent.record_id = stock_its_operations.manual_movement_id)))))) WITH CHECK ((((prepared_at IS NULL) OR (prepared_at <= clock_timestamp())) AND ((manual_movement_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.manual_movements parent
  WHERE (parent.record_id = stock_its_operations.manual_movement_id))))));


--
-- Name: takas current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.takas USING (((created_at IS NULL) OR (created_at <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text)))) WITH CHECK (((created_at IS NULL) OR (created_at <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))));


--
-- Name: takasBarcodeItems current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."takasBarcodeItems" USING (((takas_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.takas parent
  WHERE (parent.record_id = "takasBarcodeItems".takas_id))))) WITH CHECK (((takas_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.takas parent
  WHERE (parent.record_id = "takasBarcodeItems".takas_id)))));


--
-- Name: takasQrItems current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public."takasQrItems" USING (((takas_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.takas parent
  WHERE (parent.record_id = "takasQrItems".takas_id))))) WITH CHECK (((takas_id IS NULL) OR (EXISTS ( SELECT 1
   FROM public.takas parent
  WHERE (parent.record_id = "takasQrItems".takas_id)))));


--
-- Name: unnotified_its_movements current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.unnotified_its_movements USING (((created_at IS NULL) OR (created_at <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text)))) WITH CHECK (((created_at IS NULL) OR (created_at <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'::text))));


--
-- Name: users_log current_activity_only; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY current_activity_only ON public.users_log USING ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp()))) WITH CHECK ((("timestamp" IS NULL) OR ("timestamp" <= clock_timestamp())));


--
-- Name: depot_log; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.depot_log ENABLE ROW LEVEL SECURITY;

--
-- Name: doctor_log; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.doctor_log ENABLE ROW LEVEL SECURITY;

--
-- Name: faculty_log; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.faculty_log ENABLE ROW LEVEL SECURITY;

--
-- Name: family_group_log; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.family_group_log ENABLE ROW LEVEL SECURITY;

--
-- Name: invoice_incremental_sync_items; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.invoice_incremental_sync_items ENABLE ROW LEVEL SECURITY;

--
-- Name: invoice_incremental_sync_state; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.invoice_incremental_sync_state ENABLE ROW LEVEL SECURITY;

--
-- Name: invoice_line_qr_codes; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.invoice_line_qr_codes ENABLE ROW LEVEL SECURITY;

--
-- Name: invoice_lines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.invoice_lines ENABLE ROW LEVEL SECURITY;

--
-- Name: invoice_stock_log_invoices; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.invoice_stock_log_invoices ENABLE ROW LEVEL SECURITY;

--
-- Name: invoice_stock_logs; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.invoice_stock_logs ENABLE ROW LEVEL SECURITY;

--
-- Name: invoices; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.invoices ENABLE ROW LEVEL SECURITY;

--
-- Name: kurum_log; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.kurum_log ENABLE ROW LEVEL SECURITY;

--
-- Name: loyal_patient_benchmark_cohort; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.loyal_patient_benchmark_cohort ENABLE ROW LEVEL SECURITY;

--
-- Name: loyal_patient_medication_history; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.loyal_patient_medication_history ENABLE ROW LEVEL SECURITY;

--
-- Name: loyal_patient_profile; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.loyal_patient_profile ENABLE ROW LEVEL SECURITY;

--
-- Name: loyal_patient_report_history; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.loyal_patient_report_history ENABLE ROW LEVEL SECURITY;

--
-- Name: loyal_patient_report_sample_run; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.loyal_patient_report_sample_run ENABLE ROW LEVEL SECURITY;

--
-- Name: loyal_patient_report_supply_evidence; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.loyal_patient_report_supply_evidence ENABLE ROW LEVEL SECURITY;

--
-- Name: manual_movements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.manual_movements ENABLE ROW LEVEL SECURITY;

--
-- Name: patient_debt_log; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.patient_debt_log ENABLE ROW LEVEL SECURITY;

--
-- Name: patient_log; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.patient_log ENABLE ROW LEVEL SECURITY;

--
-- Name: pharmacy_log; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.pharmacy_log ENABLE ROW LEVEL SECURITY;

--
-- Name: prescription; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.prescription ENABLE ROW LEVEL SECURITY;

--
-- Name: prescription_log; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.prescription_log ENABLE ROW LEVEL SECURITY;

--
-- Name: prescription_sale_operations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.prescription_sale_operations ENABLE ROW LEVEL SECURITY;

--
-- Name: qrActive; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."qrActive" ENABLE ROW LEVEL SECURITY;

--
-- Name: qrHanged; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."qrHanged" ENABLE ROW LEVEL SECURITY;

--
-- Name: qrInactive; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."qrInactive" ENABLE ROW LEVEL SECURITY;

--
-- Name: qrMovements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."qrMovements" ENABLE ROW LEVEL SECURITY;

--
-- Name: retail_its_operation_items; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.retail_its_operation_items ENABLE ROW LEVEL SECURITY;

--
-- Name: retail_its_operations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.retail_its_operations ENABLE ROW LEVEL SECURITY;

--
-- Name: sales; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.sales ENABLE ROW LEVEL SECURITY;

--
-- Name: salesPayements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."salesPayements" ENABLE ROW LEVEL SECURITY;

--
-- Name: salesQrItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."salesQrItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: salesRefundItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."salesRefundItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: salesRefundPayements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."salesRefundPayements" ENABLE ROW LEVEL SECURITY;

--
-- Name: salesRefunds; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."salesRefunds" ENABLE ROW LEVEL SECURITY;

--
-- Name: scheduled_stock_movements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.scheduled_stock_movements ENABLE ROW LEVEL SECURITY;

--
-- Name: stock_its_operation_items; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.stock_its_operation_items ENABLE ROW LEVEL SECURITY;

--
-- Name: stock_its_operations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.stock_its_operations ENABLE ROW LEVEL SECURITY;

--
-- Name: takas; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.takas ENABLE ROW LEVEL SECURITY;

--
-- Name: takasBarcodeItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."takasBarcodeItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: takasQrItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."takasQrItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: unnotified_its_movements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.unnotified_its_movements ENABLE ROW LEVEL SECURITY;

--
-- Name: users_log; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.users_log ENABLE ROW LEVEL SECURITY;

--
-- PostgreSQL database dump complete
--

\unrestrict tY6YV5qaReYgmUChQfTgKi5brOA87WBtmasW0bNzIByarhDohxfUaqgfdgjF23b

