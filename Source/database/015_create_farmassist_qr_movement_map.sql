BEGIN;

CREATE TABLE IF NOT EXISTS public.farmassist_qr_movement_import_map (
    source_table varchar NOT NULL,
    source_movement_id integer NOT NULL,
    source_item_index integer NOT NULL CHECK (source_item_index > 0),
    target_record_id integer NOT NULL
        REFERENCES public."qrMovements" (record_id) ON DELETE CASCADE,
    imported_at timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source_table, source_movement_id, source_item_index),
    UNIQUE (target_record_id)
);

COMMIT;
