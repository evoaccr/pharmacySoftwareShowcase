BEGIN;

LOCK TABLE public.invoices, public.invoice_lines IN SHARE ROW EXCLUSIVE MODE;

CREATE TEMPORARY TABLE legacy_invoice_repairs ON COMMIT DROP AS
SELECT invoice_uuid
FROM public.invoices
WHERE kdv_amount IS NULL;

WITH explicit_due_dates AS (
    SELECT
        line.invoice_uuid,
        min(to_date((regexp_match(
            value.value,
            '(vade[[:space:]]*tarihi|deme[[:space:]]*tar)[^0-9]{0,30}([0-9]{2}[.][0-9]{2}[.][0-9]{4})',
            'i'))[2], 'DD.MM.YYYY')) AS due_date
    FROM public.invoice_lines line
    CROSS JOIN LATERAL jsonb_each_text(line.raw_data) value
    WHERE value.value ~* '(vade[[:space:]]*tarihi|deme[[:space:]]*tar)[^0-9]{0,30}[0-9]{2}[.][0-9]{2}[.][0-9]{4}'
    GROUP BY line.invoice_uuid
)
UPDATE public.invoices invoice
SET due_date = source.due_date
FROM explicit_due_dates source
WHERE invoice.invoice_uuid = source.invoice_uuid
  AND invoice.due_date IS NULL
  AND (invoice.issue_date IS NULL OR source.due_date >= invoice.issue_date);

WITH relative_due_days AS (
    SELECT
        line.invoice_uuid,
        max(value.value::integer) AS due_days
    FROM public.invoice_lines line
    CROSS JOIN LATERAL jsonb_each_text(line.raw_data) value
    WHERE lower(value.key) LIKE '%vade gun%'
      AND value.value ~ '^[0-9]+$'
    GROUP BY line.invoice_uuid
)
UPDATE public.invoices invoice
SET due_date = invoice.issue_date + source.due_days
FROM relative_due_days source
WHERE invoice.invoice_uuid = source.invoice_uuid
  AND invoice.due_date IS NULL
  AND invoice.issue_date IS NOT NULL;

WITH legacy_values AS (
    SELECT
        line.record_id,
        line.unit,
        (SELECT value.value
         FROM jsonb_each_text(line.raw_data) value
         WHERE lower(value.key) LIKE '%ktar%'
         LIMIT 1) AS quantity_text,
        (SELECT value.value
         FROM jsonb_each_text(line.raw_data) value
         WHERE upper(value.key) LIKE 'KDV%'
         LIMIT 1) AS vat_rate_text,
        (SELECT value.value
         FROM jsonb_each_text(line.raw_data) value
         WHERE lower(value.key) IN ('net tutar', 'tutar(tl)')
         LIMIT 1) AS line_amount_text
    FROM public.invoice_lines line
    JOIN legacy_invoice_repairs repair ON repair.invoice_uuid = line.invoice_uuid
),
parsed_values AS (
    SELECT
        record_id,
        CASE
            WHEN quantity_text ~ '^[0-9]+([+][0-9]+)+$' THEN
                (SELECT sum(part::numeric) FROM regexp_split_to_table(quantity_text, '[+]') part)
            WHEN quantity_text ~ '^[0-9.]+(,[0-9]+)?$' THEN
                replace(replace(quantity_text, '.', ''), ',', '.')::numeric
        END AS quantity_value,
        CASE
            WHEN unit ~ '^[0-9.]+,[0-9]+$' THEN
                replace(replace(unit, '.', ''), ',', '.')::numeric
        END AS unit_price_value,
        CASE
            WHEN vat_rate_text ~ '^[0-9.]+(,[0-9]+)?$' THEN
                replace(replace(vat_rate_text, '.', ''), ',', '.')::numeric
        END AS vat_rate_value,
        CASE
            WHEN line_amount_text ~ '^[0-9.]+(,[0-9]+)?$' THEN
                replace(replace(line_amount_text, '.', ''), ',', '.')::numeric
        END AS line_amount_value
    FROM legacy_values
)
UPDATE public.invoice_lines line
SET quantity = COALESCE(source.quantity_value, line.quantity),
    unit_price = COALESCE(source.unit_price_value, line.unit_price),
    unit = CASE WHEN source.unit_price_value IS NOT NULL THEN '' ELSE line.unit END,
    vat_rate = COALESCE(source.vat_rate_value, line.vat_rate),
    line_amount = COALESCE(source.line_amount_value, line.line_amount)
FROM parsed_values source
WHERE line.record_id = source.record_id;

UPDATE public.invoice_lines line
SET vat_amount = round(line.line_amount * line.vat_rate / 100, 4)
FROM legacy_invoice_repairs repair
WHERE line.invoice_uuid = repair.invoice_uuid
  AND line.vat_amount IS NULL
  AND line.line_amount IS NOT NULL
  AND line.vat_rate IS NOT NULL;

UPDATE public.invoices invoice
SET total_cost = invoice.total_cost / 100,
    payable_amount = invoice.payable_amount / 100
FROM legacy_invoice_repairs repair
WHERE invoice.invoice_uuid = repair.invoice_uuid;

WITH invoice_taxes AS (
    SELECT line.invoice_uuid, round(sum(line.vat_amount), 2) AS tax_amount
    FROM public.invoice_lines line
    JOIN legacy_invoice_repairs repair ON repair.invoice_uuid = line.invoice_uuid
    WHERE line.vat_amount IS NOT NULL
    GROUP BY line.invoice_uuid
)
UPDATE public.invoices invoice
SET kdv_amount = source.tax_amount
FROM invoice_taxes source
WHERE invoice.invoice_uuid = source.invoice_uuid;

UPDATE public.invoices invoice
SET kdv_amount = round(invoice.payable_amount - invoice.total_cost, 2)
FROM legacy_invoice_repairs repair
WHERE invoice.invoice_uuid = repair.invoice_uuid
  AND invoice.kdv_amount IS NULL
  AND invoice.payable_amount >= invoice.total_cost;

UPDATE public.invoice_lines
SET is_medication = true
WHERE NOT is_medication
  AND raw_data::text ~* '[(]GTIN[)].*[(]SN[)]';

UPDATE public.invoice_lines
SET description = ''
WHERE is_medication
  AND description <> ''
  AND upper(trim(description)) = upper(trim(item_name));

DELETE FROM public.invoice_lines
WHERE item_code = ''
  AND barcode = ''
  AND item_name = ''
  AND description = ''
  AND quantity IS NULL
  AND unit = ''
  AND unit_price IS NULL
  AND line_amount IS NULL;

COMMIT;
