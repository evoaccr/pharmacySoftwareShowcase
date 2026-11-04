BEGIN;
SET LOCAL lock_timeout = '5s';

UPDATE public.invoice_stock_logs log
SET action_code = CASE log.action_code
        WHEN 'UNSAVE' THEN 'REFUND'
        WHEN 'REFUND_CANCEL' THEN 'SAVE'
        ELSE log.action_code
    END,
    movement_type_id = CASE log.action_code
        WHEN 'UNSAVE' THEN (SELECT record_id FROM public."qrMovementTypes" WHERE code = 'DEP_RETURN')
        WHEN 'REFUND_CANCEL' THEN (SELECT record_id FROM public."qrMovementTypes" WHERE code = 'DEP_IN')
        ELSE log.movement_type_id
    END
WHERE log.action_code IN ('UNSAVE', 'REFUND_CANCEL');

ALTER TABLE public.invoice_stock_logs
    DROP CONSTRAINT IF EXISTS invoice_stock_logs_action_check;
ALTER TABLE public.invoice_stock_logs
    ADD CONSTRAINT invoice_stock_logs_action_check
    CHECK (action_code IN ('SAVE', 'REFUND', 'PARTIAL_REFUND'));

DROP TRIGGER IF EXISTS trg_qrMovements_apply_invoice_unsave ON public."qrMovements";
DROP FUNCTION IF EXISTS public.pharmacy_apply_invoice_unsave_movement();

DROP TRIGGER IF EXISTS "trg_qrMovements_apply_stock" ON public."qrMovements";
DROP TRIGGER IF EXISTS trg_qrMovements_apply_stock ON public."qrMovements";
CREATE TRIGGER trg_qrMovements_apply_stock
AFTER INSERT ON public."qrMovements"
FOR EACH ROW
EXECUTE FUNCTION public.pharmacy_apply_qr_movement();

UPDATE public."qrMovements" movement
SET movement_type = CASE
        WHEN obsolete.code IN ('DEP_REFUND_CANCEL', 'INV_REFUND_CANCEL') THEN depot_in.record_id
        ELSE depot_return.record_id
    END
FROM public."qrMovementTypes" obsolete,
     public."qrMovementTypes" depot_in,
     public."qrMovementTypes" depot_return
WHERE movement.movement_type = obsolete.record_id
  AND obsolete.code IN (
      'DEP_IN_CANCEL', 'DEP_REFUND_CANCEL', 'INV_UNSAVE', 'INV_REFUND_CANCEL')
  AND depot_in.code = 'DEP_IN'
  AND depot_return.code = 'DEP_RETURN';

UPDATE public.invoice_stock_logs log
SET movement_type_id = CASE
        WHEN obsolete.code IN ('DEP_REFUND_CANCEL', 'INV_REFUND_CANCEL') THEN depot_in.record_id
        ELSE depot_return.record_id
    END
FROM public."qrMovementTypes" obsolete,
     public."qrMovementTypes" depot_in,
     public."qrMovementTypes" depot_return
WHERE log.movement_type_id = obsolete.record_id
  AND obsolete.code IN (
      'DEP_IN_CANCEL', 'DEP_REFUND_CANCEL', 'INV_UNSAVE', 'INV_REFUND_CANCEL')
  AND depot_in.code = 'DEP_IN'
  AND depot_return.code = 'DEP_RETURN';

DELETE FROM public."qrMovementTypes" movement_type
WHERE movement_type.code IN (
    'DEP_IN_CANCEL', 'DEP_REFUND_CANCEL', 'INV_UNSAVE', 'INV_REFUND_CANCEL')
  AND NOT EXISTS (
      SELECT 1 FROM public."qrMovements" movement
      WHERE movement.movement_type = movement_type.record_id)
  AND NOT EXISTS (
      SELECT 1 FROM public.invoice_stock_logs log
      WHERE log.movement_type_id = movement_type.record_id);

COMMIT;
