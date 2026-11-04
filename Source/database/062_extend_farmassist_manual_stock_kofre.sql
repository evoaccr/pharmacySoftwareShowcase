BEGIN;

ALTER TABLE public.farmassist_manual_movement_import_map
    DROP CONSTRAINT IF EXISTS farmassist_manual_movement_import_map_source_movement_type_check;

ALTER TABLE public.farmassist_manual_movement_import_map
    ADD CONSTRAINT farmassist_manual_movement_import_map_source_movement_type_check
        CHECK (source_movement_type IN (9, 10, 18, 19, 23, 24, 100, 101, 102, 103));

COMMIT;
