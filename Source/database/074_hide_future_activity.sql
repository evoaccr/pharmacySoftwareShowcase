CREATE TABLE IF NOT EXISTS public.scheduled_stock_movements(record_id integer PRIMARY KEY REFERENCES public."qrMovements"(record_id) ON DELETE CASCADE);
CREATE OR REPLACE FUNCTION public.pharmacy_apply_qr_movement_record(movement public."qrMovements") RETURNS void LANGUAGE plpgsql AS $function$
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
$function$;
CREATE OR REPLACE FUNCTION public.pharmacy_apply_qr_movement() RETURNS trigger LANGUAGE plpgsql AS $function$
BEGIN
 IF NEW.movement_date > (clock_timestamp() AT TIME ZONE 'Europe/Istanbul') THEN
  INSERT INTO public.scheduled_stock_movements(record_id) VALUES(NEW.record_id) ON CONFLICT DO NOTHING;
 ELSE
  PERFORM public.pharmacy_apply_qr_movement_record(NEW);
 END IF;
 RETURN NEW;
END $function$;
CREATE INDEX IF NOT EXISTS ix_qr_movements_date_record ON public."qrMovements"(movement_date,record_id);
CREATE INDEX IF NOT EXISTS ix_qr_movements_code_date ON public."qrMovements"(qr_code,movement_date);
CREATE INDEX IF NOT EXISTS ix_active_barcode_code ON public."qrActive"(barcode,qr_code);
CREATE INDEX IF NOT EXISTS ix_inactive_barcode_code ON public."qrInactive"(barcode,qr_code);
CREATE INDEX IF NOT EXISTS ix_hanged_barcode_code ON public."qrHanged"(barcode,qr_code);
CREATE OR REPLACE FUNCTION public.track_patient_prescription_count()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
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
END $function$
;
ALTER TABLE public."barcodeAnalysis" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."barcodeAnalysis" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."barcodeAnalysis";
CREATE POLICY current_activity_only ON public."barcodeAnalysis" USING (("updated_at" IS NULL OR "updated_at" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'))) WITH CHECK (("updated_at" IS NULL OR "updated_at" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')));
ALTER TABLE public."barcodeCampaign" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."barcodeCampaign" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."barcodeCampaign";
CREATE POLICY current_activity_only ON public."barcodeCampaign" USING (("start_date" IS NULL OR "start_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date)) WITH CHECK (("start_date" IS NULL OR "start_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date));
ALTER TABLE public."barcodePriceHistory" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."barcodePriceHistory" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."barcodePriceHistory";
CREATE POLICY current_activity_only ON public."barcodePriceHistory" USING (("effective_date" IS NULL OR "effective_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date)) WITH CHECK (("effective_date" IS NULL OR "effective_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date));
ALTER TABLE public."consignmentSaleItems" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."consignmentSaleItems" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."consignmentSaleItems";
CREATE POLICY current_activity_only ON public."consignmentSaleItems" USING (("consignment_sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."consignmentSales" parent WHERE parent."record_id"="consignmentSaleItems"."consignment_sale_id"))) WITH CHECK (("consignment_sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."consignmentSales" parent WHERE parent."record_id"="consignmentSaleItems"."consignment_sale_id")));
ALTER TABLE public."consignmentSalePayements" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."consignmentSalePayements" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."consignmentSalePayements";
CREATE POLICY current_activity_only ON public."consignmentSalePayements" USING (("consignment_sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."consignmentSales" parent WHERE parent."record_id"="consignmentSalePayements"."consignment_sale_id"))) WITH CHECK (("consignment_sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."consignmentSales" parent WHERE parent."record_id"="consignmentSalePayements"."consignment_sale_id")));
ALTER TABLE public."consignmentSales" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."consignmentSales" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."consignmentSales";
CREATE POLICY current_activity_only ON public."consignmentSales" USING (("timestamp" IS NULL OR "timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')) AND ("settlement_sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="consignmentSales"."settlement_sale_id"))) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')) AND ("settlement_sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="consignmentSales"."settlement_sale_id")));
ALTER TABLE public."depot_log" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."depot_log" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."depot_log";
CREATE POLICY current_activity_only ON public."depot_log" USING (("timestamp" IS NULL OR "timestamp" <= clock_timestamp())) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= clock_timestamp()));
ALTER TABLE public."doctor_log" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."doctor_log" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."doctor_log";
CREATE POLICY current_activity_only ON public."doctor_log" USING (("timestamp" IS NULL OR "timestamp" <= clock_timestamp())) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= clock_timestamp()));
ALTER TABLE public."faculty_log" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."faculty_log" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."faculty_log";
CREATE POLICY current_activity_only ON public."faculty_log" USING (("timestamp" IS NULL OR "timestamp" <= clock_timestamp())) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= clock_timestamp()));
ALTER TABLE public."family_group_log" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."family_group_log" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."family_group_log";
CREATE POLICY current_activity_only ON public."family_group_log" USING (("timestamp" IS NULL OR "timestamp" <= clock_timestamp())) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= clock_timestamp()));
ALTER TABLE public."invoice_incremental_sync_items" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."invoice_incremental_sync_items" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."invoice_incremental_sync_items";
CREATE POLICY current_activity_only ON public."invoice_incremental_sync_items" USING (("received_at" IS NULL OR "received_at" <= clock_timestamp()) AND ("issue_date" IS NULL OR "issue_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date)) WITH CHECK (("received_at" IS NULL OR "received_at" <= clock_timestamp()) AND ("issue_date" IS NULL OR "issue_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date));
ALTER TABLE public."invoice_incremental_sync_state" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."invoice_incremental_sync_state" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."invoice_incremental_sync_state";
CREATE POLICY current_activity_only ON public."invoice_incremental_sync_state" USING (("updated_at" IS NULL OR "updated_at" <= clock_timestamp())) WITH CHECK (("updated_at" IS NULL OR "updated_at" <= clock_timestamp()));
ALTER TABLE public."invoice_line_qr_codes" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."invoice_line_qr_codes" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."invoice_line_qr_codes";
CREATE POLICY current_activity_only ON public."invoice_line_qr_codes" USING (("invoice_line_id" IS NULL OR EXISTS (SELECT 1 FROM public."invoice_lines" parent WHERE parent."record_id"="invoice_line_qr_codes"."invoice_line_id"))) WITH CHECK (("invoice_line_id" IS NULL OR EXISTS (SELECT 1 FROM public."invoice_lines" parent WHERE parent."record_id"="invoice_line_qr_codes"."invoice_line_id")));
ALTER TABLE public."invoice_lines" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."invoice_lines" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."invoice_lines";
CREATE POLICY current_activity_only ON public."invoice_lines" USING (("invoice_uuid" IS NULL OR EXISTS (SELECT 1 FROM public."invoices" parent WHERE parent."invoice_uuid"="invoice_lines"."invoice_uuid"))) WITH CHECK (("invoice_uuid" IS NULL OR EXISTS (SELECT 1 FROM public."invoices" parent WHERE parent."invoice_uuid"="invoice_lines"."invoice_uuid")));
ALTER TABLE public."invoice_stock_log_invoices" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."invoice_stock_log_invoices" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."invoice_stock_log_invoices";
CREATE POLICY current_activity_only ON public."invoice_stock_log_invoices" USING (("invoice_stock_log_id" IS NULL OR EXISTS (SELECT 1 FROM public."invoice_stock_logs" parent WHERE parent."record_id"="invoice_stock_log_invoices"."invoice_stock_log_id")) AND ("invoice_uuid" IS NULL OR EXISTS (SELECT 1 FROM public."invoices" parent WHERE parent."invoice_uuid"="invoice_stock_log_invoices"."invoice_uuid"))) WITH CHECK (("invoice_stock_log_id" IS NULL OR EXISTS (SELECT 1 FROM public."invoice_stock_logs" parent WHERE parent."record_id"="invoice_stock_log_invoices"."invoice_stock_log_id")) AND ("invoice_uuid" IS NULL OR EXISTS (SELECT 1 FROM public."invoices" parent WHERE parent."invoice_uuid"="invoice_stock_log_invoices"."invoice_uuid")));
ALTER TABLE public."invoice_stock_logs" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."invoice_stock_logs" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."invoice_stock_logs";
CREATE POLICY current_activity_only ON public."invoice_stock_logs" USING (("occurred_at" IS NULL OR "occurred_at" <= clock_timestamp()) AND ("invoice_uuid" IS NULL OR EXISTS (SELECT 1 FROM public."invoices" parent WHERE parent."invoice_uuid"="invoice_stock_logs"."invoice_uuid"))) WITH CHECK (("occurred_at" IS NULL OR "occurred_at" <= clock_timestamp()) AND ("invoice_uuid" IS NULL OR EXISTS (SELECT 1 FROM public."invoices" parent WHERE parent."invoice_uuid"="invoice_stock_logs"."invoice_uuid")));
ALTER TABLE public."invoices" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."invoices" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."invoices";
CREATE POLICY current_activity_only ON public."invoices" USING (("database_entry_at" IS NULL OR "database_entry_at" <= clock_timestamp()) AND ("issue_date" IS NULL OR "issue_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date)) WITH CHECK (("database_entry_at" IS NULL OR "database_entry_at" <= clock_timestamp()) AND ("issue_date" IS NULL OR "issue_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date));
ALTER TABLE public."kurum_log" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."kurum_log" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."kurum_log";
CREATE POLICY current_activity_only ON public."kurum_log" USING (("timestamp" IS NULL OR "timestamp" <= clock_timestamp())) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= clock_timestamp()));
ALTER TABLE public."loyal_patient_benchmark_cohort" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."loyal_patient_benchmark_cohort" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."loyal_patient_benchmark_cohort";
CREATE POLICY current_activity_only ON public."loyal_patient_benchmark_cohort" USING (("selected_at" IS NULL OR "selected_at" <= clock_timestamp())) WITH CHECK (("selected_at" IS NULL OR "selected_at" <= clock_timestamp()));
ALTER TABLE public."loyal_patient_medication_history" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."loyal_patient_medication_history" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."loyal_patient_medication_history";
CREATE POLICY current_activity_only ON public."loyal_patient_medication_history" USING (("medication_purchase_date" IS NULL OR "medication_purchase_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date) AND ("synced_at" IS NULL OR "synced_at" <= clock_timestamp()) AND ("patient_record_id" IS NULL OR EXISTS (SELECT 1 FROM public."loyal_patient_profile" parent WHERE parent."patient_record_id"="loyal_patient_medication_history"."patient_record_id"))) WITH CHECK (("medication_purchase_date" IS NULL OR "medication_purchase_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date) AND ("synced_at" IS NULL OR "synced_at" <= clock_timestamp()) AND ("patient_record_id" IS NULL OR EXISTS (SELECT 1 FROM public."loyal_patient_profile" parent WHERE parent."patient_record_id"="loyal_patient_medication_history"."patient_record_id")));
ALTER TABLE public."loyal_patient_profile" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."loyal_patient_profile" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."loyal_patient_profile";
CREATE POLICY current_activity_only ON public."loyal_patient_profile" USING (("medication_history_synced_at" IS NULL OR "medication_history_synced_at" <= clock_timestamp()) AND ("report_history_synced_at" IS NULL OR "report_history_synced_at" <= clock_timestamp())) WITH CHECK (("medication_history_synced_at" IS NULL OR "medication_history_synced_at" <= clock_timestamp()) AND ("report_history_synced_at" IS NULL OR "report_history_synced_at" <= clock_timestamp()));
ALTER TABLE public."loyal_patient_report_history" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."loyal_patient_report_history" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."loyal_patient_report_history";
CREATE POLICY current_activity_only ON public."loyal_patient_report_history" USING (("start_date" IS NULL OR "start_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date) AND ("synced_at" IS NULL OR "synced_at" <= clock_timestamp()) AND ("patient_record_id" IS NULL OR EXISTS (SELECT 1 FROM public."loyal_patient_profile" parent WHERE parent."patient_record_id"="loyal_patient_report_history"."patient_record_id"))) WITH CHECK (("start_date" IS NULL OR "start_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date) AND ("synced_at" IS NULL OR "synced_at" <= clock_timestamp()) AND ("patient_record_id" IS NULL OR EXISTS (SELECT 1 FROM public."loyal_patient_profile" parent WHERE parent."patient_record_id"="loyal_patient_report_history"."patient_record_id")));
ALTER TABLE public."loyal_patient_report_sample_run" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."loyal_patient_report_sample_run" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."loyal_patient_report_sample_run";
CREATE POLICY current_activity_only ON public."loyal_patient_report_sample_run" USING (("started_at" IS NULL OR "started_at" <= clock_timestamp())) WITH CHECK (("started_at" IS NULL OR "started_at" <= clock_timestamp()));
ALTER TABLE public."loyal_patient_report_supply_evidence" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."loyal_patient_report_supply_evidence" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."loyal_patient_report_supply_evidence";
CREATE POLICY current_activity_only ON public."loyal_patient_report_supply_evidence" USING (("evaluated_at" IS NULL OR "evaluated_at" <= clock_timestamp()) AND ("run_id" IS NULL OR EXISTS (SELECT 1 FROM public."loyal_patient_report_sample_run" parent WHERE parent."run_id"="loyal_patient_report_supply_evidence"."run_id"))) WITH CHECK (("evaluated_at" IS NULL OR "evaluated_at" <= clock_timestamp()) AND ("run_id" IS NULL OR EXISTS (SELECT 1 FROM public."loyal_patient_report_sample_run" parent WHERE parent."run_id"="loyal_patient_report_supply_evidence"."run_id")));
ALTER TABLE public."manual_movements" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."manual_movements" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."manual_movements";
CREATE POLICY current_activity_only ON public."manual_movements" USING (("timestamp" IS NULL OR "timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'))) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')));
ALTER TABLE public."patient_debt_log" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."patient_debt_log" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."patient_debt_log";
CREATE POLICY current_activity_only ON public."patient_debt_log" USING (("timestamp" IS NULL OR "timestamp" <= clock_timestamp())) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= clock_timestamp()));
ALTER TABLE public."patient_log" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."patient_log" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."patient_log";
CREATE POLICY current_activity_only ON public."patient_log" USING (("timestamp" IS NULL OR "timestamp" <= clock_timestamp())) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= clock_timestamp()));
ALTER TABLE public."pharmacy_log" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."pharmacy_log" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."pharmacy_log";
CREATE POLICY current_activity_only ON public."pharmacy_log" USING (("timestamp" IS NULL OR "timestamp" <= clock_timestamp())) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= clock_timestamp()));
ALTER TABLE public."prescription" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."prescription" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."prescription";
CREATE POLICY current_activity_only ON public."prescription" USING (("saved_at" IS NULL OR "saved_at" <= clock_timestamp()) AND ("prescription_date" IS NULL OR "prescription_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date) AND ("medication_purchase_date" IS NULL OR "medication_purchase_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date) AND ("medula_filter_date" IS NULL OR "medula_filter_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date)) WITH CHECK (("saved_at" IS NULL OR "saved_at" <= clock_timestamp()) AND ("prescription_date" IS NULL OR "prescription_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date) AND ("medication_purchase_date" IS NULL OR "medication_purchase_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date) AND ("medula_filter_date" IS NULL OR "medula_filter_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date));
ALTER TABLE public."prescription_log" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."prescription_log" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."prescription_log";
CREATE POLICY current_activity_only ON public."prescription_log" USING (("update_datetime" IS NULL OR "update_datetime" <= clock_timestamp()) AND (NULLIF(prescription_snapshot->>'saved_at','') IS NULL OR (prescription_snapshot->>'saved_at')::timestamptz<=clock_timestamp()) AND (NULLIF(prescription_snapshot->>'prescription_date','') IS NULL OR (prescription_snapshot->>'prescription_date')::date<=(clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date)) WITH CHECK (("update_datetime" IS NULL OR "update_datetime" <= clock_timestamp()) AND (NULLIF(prescription_snapshot->>'saved_at','') IS NULL OR (prescription_snapshot->>'saved_at')::timestamptz<=clock_timestamp()) AND (NULLIF(prescription_snapshot->>'prescription_date','') IS NULL OR (prescription_snapshot->>'prescription_date')::date<=(clock_timestamp() AT TIME ZONE 'Europe/Istanbul')::date));
ALTER TABLE public."prescription_sale_operations" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."prescription_sale_operations" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."prescription_sale_operations";
CREATE POLICY current_activity_only ON public."prescription_sale_operations" USING (("prepared_at" IS NULL OR "prepared_at" <= clock_timestamp()) AND ("sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="prescription_sale_operations"."sale_id"))) WITH CHECK (("prepared_at" IS NULL OR "prepared_at" <= clock_timestamp()) AND ("sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="prescription_sale_operations"."sale_id")));
ALTER TABLE public."qrActive" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."qrActive" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."qrActive";
CREATE POLICY current_activity_only ON public."qrActive" USING (EXISTS (SELECT 1 FROM public."qrMovements" movement WHERE movement.qr_code="qrActive".qr_code)) WITH CHECK (EXISTS (SELECT 1 FROM public."qrMovements" movement WHERE movement.qr_code="qrActive".qr_code));
ALTER TABLE public."qrHanged" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."qrHanged" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."qrHanged";
CREATE POLICY current_activity_only ON public."qrHanged" USING (("hanged_at" IS NULL OR "hanged_at" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')) AND ("takas_item_id" IS NULL OR EXISTS (SELECT 1 FROM public."takasQrItems" parent WHERE parent."record_id"="qrHanged"."takas_item_id"))) WITH CHECK (("hanged_at" IS NULL OR "hanged_at" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')) AND ("takas_item_id" IS NULL OR EXISTS (SELECT 1 FROM public."takasQrItems" parent WHERE parent."record_id"="qrHanged"."takas_item_id")));
ALTER TABLE public."qrInactive" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."qrInactive" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."qrInactive";
CREATE POLICY current_activity_only ON public."qrInactive" USING (EXISTS (SELECT 1 FROM public."qrMovements" movement WHERE movement.qr_code="qrInactive".qr_code)) WITH CHECK (EXISTS (SELECT 1 FROM public."qrMovements" movement WHERE movement.qr_code="qrInactive".qr_code));
ALTER TABLE public."qrMovements" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."qrMovements" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."qrMovements";
CREATE POLICY current_activity_only ON public."qrMovements" USING (("movement_date" IS NULL OR "movement_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'))) WITH CHECK (("movement_date" IS NULL OR "movement_date" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')));
ALTER TABLE public."retail_its_operation_items" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."retail_its_operation_items" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."retail_its_operation_items";
CREATE POLICY current_activity_only ON public."retail_its_operation_items" USING (("operation_id" IS NULL OR EXISTS (SELECT 1 FROM public."retail_its_operations" parent WHERE parent."operation_id"="retail_its_operation_items"."operation_id"))) WITH CHECK (("operation_id" IS NULL OR EXISTS (SELECT 1 FROM public."retail_its_operations" parent WHERE parent."operation_id"="retail_its_operation_items"."operation_id")));
ALTER TABLE public."retail_its_operations" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."retail_its_operations" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."retail_its_operations";
CREATE POLICY current_activity_only ON public."retail_its_operations" USING (("prepared_at" IS NULL OR "prepared_at" <= clock_timestamp()) AND ("refund_id" IS NULL OR EXISTS (SELECT 1 FROM public."salesRefunds" parent WHERE parent."record_id"="retail_its_operations"."refund_id")) AND ("sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="retail_its_operations"."sale_id"))) WITH CHECK (("prepared_at" IS NULL OR "prepared_at" <= clock_timestamp()) AND ("refund_id" IS NULL OR EXISTS (SELECT 1 FROM public."salesRefunds" parent WHERE parent."record_id"="retail_its_operations"."refund_id")) AND ("sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="retail_its_operations"."sale_id")));
ALTER TABLE public."sales" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."sales" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."sales";
CREATE POLICY current_activity_only ON public."sales" USING (("timestamp" IS NULL OR "timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'))) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')));
ALTER TABLE public."salesPayements" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."salesPayements" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."salesPayements";
CREATE POLICY current_activity_only ON public."salesPayements" USING (("sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="salesPayements"."sale_id"))) WITH CHECK (("sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="salesPayements"."sale_id")));
ALTER TABLE public."salesQrItems" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."salesQrItems" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."salesQrItems";
CREATE POLICY current_activity_only ON public."salesQrItems" USING (("sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="salesQrItems"."sale_id"))) WITH CHECK (("sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="salesQrItems"."sale_id")));
ALTER TABLE public."salesRefundItems" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."salesRefundItems" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."salesRefundItems";
CREATE POLICY current_activity_only ON public."salesRefundItems" USING (("original_sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="salesRefundItems"."original_sale_id")) AND ("refund_id" IS NULL OR EXISTS (SELECT 1 FROM public."salesRefunds" parent WHERE parent."record_id"="salesRefundItems"."refund_id"))) WITH CHECK (("original_sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="salesRefundItems"."original_sale_id")) AND ("refund_id" IS NULL OR EXISTS (SELECT 1 FROM public."salesRefunds" parent WHERE parent."record_id"="salesRefundItems"."refund_id")));
ALTER TABLE public."salesRefundPayements" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."salesRefundPayements" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."salesRefundPayements";
CREATE POLICY current_activity_only ON public."salesRefundPayements" USING (("refund_id" IS NULL OR EXISTS (SELECT 1 FROM public."salesRefunds" parent WHERE parent."record_id"="salesRefundPayements"."refund_id"))) WITH CHECK (("refund_id" IS NULL OR EXISTS (SELECT 1 FROM public."salesRefunds" parent WHERE parent."record_id"="salesRefundPayements"."refund_id")));
ALTER TABLE public."salesRefunds" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."salesRefunds" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."salesRefunds";
CREATE POLICY current_activity_only ON public."salesRefunds" USING (("timestamp" IS NULL OR "timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')) AND ("original_sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="salesRefunds"."original_sale_id"))) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')) AND ("original_sale_id" IS NULL OR EXISTS (SELECT 1 FROM public."sales" parent WHERE parent."record_id"="salesRefunds"."original_sale_id")));
ALTER TABLE public."scheduled_stock_movements" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."scheduled_stock_movements" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."scheduled_stock_movements";
CREATE POLICY current_activity_only ON public."scheduled_stock_movements" USING (EXISTS (SELECT 1 FROM public."qrMovements" movement WHERE movement.record_id=scheduled_stock_movements.record_id)) WITH CHECK (EXISTS (SELECT 1 FROM public."qrMovements" movement WHERE movement.record_id=scheduled_stock_movements.record_id));
ALTER TABLE public."stock_its_operation_items" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."stock_its_operation_items" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."stock_its_operation_items";
CREATE POLICY current_activity_only ON public."stock_its_operation_items" USING (("operation_id" IS NULL OR EXISTS (SELECT 1 FROM public."stock_its_operations" parent WHERE parent."operation_id"="stock_its_operation_items"."operation_id"))) WITH CHECK (("operation_id" IS NULL OR EXISTS (SELECT 1 FROM public."stock_its_operations" parent WHERE parent."operation_id"="stock_its_operation_items"."operation_id")));
ALTER TABLE public."stock_its_operations" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."stock_its_operations" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."stock_its_operations";
CREATE POLICY current_activity_only ON public."stock_its_operations" USING (("prepared_at" IS NULL OR "prepared_at" <= clock_timestamp()) AND ("manual_movement_id" IS NULL OR EXISTS (SELECT 1 FROM public."manual_movements" parent WHERE parent."record_id"="stock_its_operations"."manual_movement_id"))) WITH CHECK (("prepared_at" IS NULL OR "prepared_at" <= clock_timestamp()) AND ("manual_movement_id" IS NULL OR EXISTS (SELECT 1 FROM public."manual_movements" parent WHERE parent."record_id"="stock_its_operations"."manual_movement_id")));
ALTER TABLE public."takas" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."takas" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."takas";
CREATE POLICY current_activity_only ON public."takas" USING (("created_at" IS NULL OR "created_at" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'))) WITH CHECK (("created_at" IS NULL OR "created_at" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')));
ALTER TABLE public."takasBarcodeItems" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."takasBarcodeItems" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."takasBarcodeItems";
CREATE POLICY current_activity_only ON public."takasBarcodeItems" USING (("takas_id" IS NULL OR EXISTS (SELECT 1 FROM public."takas" parent WHERE parent."record_id"="takasBarcodeItems"."takas_id"))) WITH CHECK (("takas_id" IS NULL OR EXISTS (SELECT 1 FROM public."takas" parent WHERE parent."record_id"="takasBarcodeItems"."takas_id")));
ALTER TABLE public."takasQrItems" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."takasQrItems" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."takasQrItems";
CREATE POLICY current_activity_only ON public."takasQrItems" USING (("takas_id" IS NULL OR EXISTS (SELECT 1 FROM public."takas" parent WHERE parent."record_id"="takasQrItems"."takas_id"))) WITH CHECK (("takas_id" IS NULL OR EXISTS (SELECT 1 FROM public."takas" parent WHERE parent."record_id"="takasQrItems"."takas_id")));
ALTER TABLE public."unnotified_its_movements" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."unnotified_its_movements" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."unnotified_its_movements";
CREATE POLICY current_activity_only ON public."unnotified_its_movements" USING (("created_at" IS NULL OR "created_at" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul'))) WITH CHECK (("created_at" IS NULL OR "created_at" <= (clock_timestamp() AT TIME ZONE 'Europe/Istanbul')));
ALTER TABLE public."users_log" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."users_log" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS current_activity_only ON public."users_log";
CREATE POLICY current_activity_only ON public."users_log" USING (("timestamp" IS NULL OR "timestamp" <= clock_timestamp())) WITH CHECK (("timestamp" IS NULL OR "timestamp" <= clock_timestamp()));
GRANT SELECT ON public.scheduled_stock_movements TO portfolio;
CREATE OR REPLACE FUNCTION public.refresh_visible_activity() RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $function$
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
END $function$;
REVOKE ALL ON FUNCTION public.refresh_visible_activity() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.refresh_visible_activity() TO portfolio;
REVOKE ALL ON FUNCTION public.pharmacy_apply_qr_movement_record(public."qrMovements") FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.pharmacy_apply_qr_movement_record(public."qrMovements") TO portfolio;
