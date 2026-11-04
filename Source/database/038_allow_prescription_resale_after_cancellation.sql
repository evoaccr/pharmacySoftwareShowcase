BEGIN;

-- A completed ITS cancellation/refund is an auditable historical sale attempt.
-- Keep it intact and allow one new active attempt for the same prescription.
DROP INDEX IF EXISTS public.ux_sales_prescription_uid;

CREATE UNIQUE INDEX ux_sales_prescription_uid
    ON public.sales (prescription_uid)
    WHERE prescription_uid IS NOT NULL
      AND sale_status <> 'cancelled';

-- Older builds replaced patient allocations before noticing that the sale had
-- already been cancelled. Close any such rows so they cannot appear as open
-- receivables on the preserved historical attempt.
UPDATE public."salesPayements" payment
   SET cancelled_at = COALESCE(payment.cancelled_at, CURRENT_TIMESTAMP)
  FROM public.sales sale
 WHERE sale.record_id = payment.sale_id
   AND sale.sale_status = 'cancelled'
   AND payment.cancelled_at IS NULL;

COMMIT;
