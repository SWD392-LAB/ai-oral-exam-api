-- Forward-only upgrade for existing databases. Fresh databases get these objects from 01_schema.sql.
BEGIN;

SELECT set_config('aives.application_schema', :'app_schema', true);

DO $$
DECLARE
    v_schema text := NULLIF(current_setting('aives.application_schema', true), '');
BEGIN
    IF v_schema IS NULL THEN
        RAISE EXCEPTION 'Set the required psql variable app_schema to the application schema and retry.';
    END IF;

    EXECUTE format($sql$
        CREATE TABLE IF NOT EXISTS %I.course_materials (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            course_id UUID NOT NULL REFERENCES %I.courses(id) ON DELETE CASCADE,
            file_name VARCHAR(255) NOT NULL,
            content_type VARCHAR(100) NOT NULL,
            size_bytes BIGINT NOT NULL CHECK (size_bytes > 0),
            sha256 VARCHAR(64) NOT NULL,
            storage_key VARCHAR(500) NOT NULL,
            status VARCHAR(20) NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Ready','Failed','Archived')),
            ingest_error VARCHAR(500),
            created_by UUID NOT NULL REFERENCES %I.users(id),
            created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        )
    $sql$, v_schema, v_schema, v_schema);
    EXECUTE format('CREATE INDEX IF NOT EXISTS ix_course_materials_course_status ON %I.course_materials(course_id, status)', v_schema);
    EXECUTE format($sql$
        CREATE TABLE IF NOT EXISTS %I.material_chunks (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            course_material_id UUID NOT NULL REFERENCES %I.course_materials(id) ON DELETE CASCADE,
            chunk_no INT NOT NULL CHECK (chunk_no > 0),
            citation VARCHAR(300) NOT NULL,
            text TEXT NOT NULL,
            UNIQUE (course_material_id, chunk_no)
        )
    $sql$, v_schema, v_schema);
    EXECUTE format('CREATE INDEX IF NOT EXISTS ix_material_chunks_material ON %I.material_chunks(course_material_id)', v_schema);
END;
$$;

COMMIT;
