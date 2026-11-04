BEGIN;

CREATE TABLE IF NOT EXISTS public.farmassist_import_map (
    source_table varchar NOT NULL,
    source_id integer NOT NULL,
    target_table varchar NOT NULL,
    target_record_id integer NOT NULL,
    PRIMARY KEY (source_table, source_id),
    UNIQUE (target_table, target_record_id),
    CONSTRAINT ck_farmassist_import_map_target
        CHECK (target_table IN ('depot', 'doctor', 'pharmacy', 'patient', 'faculty', 'kurum'))
);

CREATE TABLE IF NOT EXISTS public.importer_marked (
    table_name varchar NOT NULL,
    table_record_id integer NOT NULL,
    mark varchar NOT NULL,
    resolved boolean NOT NULL DEFAULT FALSE,
    CONSTRAINT pk_importer_marked
        PRIMARY KEY (table_name, table_record_id, mark),
    CONSTRAINT ck_importer_marked_table
        CHECK (table_name IN ('depot', 'doctor', 'pharmacy', 'patient', 'faculty', 'kurum')),
    CONSTRAINT ck_importer_marked_mark
        CHECK (mark IN ('critical incomplete', 'incomplete', 'missing identity')),
    CONSTRAINT fk_importer_marked_import_map
        FOREIGN KEY (table_name, table_record_id)
        REFERENCES public.farmassist_import_map (target_table, target_record_id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_importer_marked_unresolved
    ON public.importer_marked (table_name, table_record_id)
    WHERE resolved=FALSE;

COMMIT;
