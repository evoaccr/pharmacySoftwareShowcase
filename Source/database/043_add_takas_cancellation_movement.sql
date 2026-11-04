BEGIN;

INSERT INTO public."qrMovementTypes" (record_id, code, display_name, is_entry)
VALUES (20, 'TRANSF_CANCEL', 'Takas İptal', TRUE)
ON CONFLICT (record_id) DO UPDATE
SET code = EXCLUDED.code,
    display_name = EXCLUDED.display_name,
    is_entry = EXCLUDED.is_entry;

COMMIT;
