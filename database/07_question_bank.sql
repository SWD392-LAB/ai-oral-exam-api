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
        CREATE TABLE IF NOT EXISTS %I.question_bank_items (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            course_id UUID NOT NULL REFERENCES %I.courses(id) ON DELETE CASCADE,
            topic VARCHAR(200) NOT NULL,
            bloom VARCHAR(30) NOT NULL CHECK (bloom IN ('Remember','Understand','Apply','Analyze')),
            content TEXT NOT NULL,
            origin VARCHAR(20) NOT NULL CHECK (origin IN ('Manual','Import','Ai')),
            status VARCHAR(20) NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Approved','Rejected','Archived')),
            created_by UUID NOT NULL REFERENCES %I.users(id),
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        )
    $sql$, v_schema, v_schema, v_schema);
    EXECUTE format('CREATE INDEX IF NOT EXISTS ix_question_bank_course_status ON %I.question_bank_items(course_id, status)', v_schema);
    EXECUTE format($sql$
        CREATE TABLE IF NOT EXISTS %I.question_bank_revisions (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            question_bank_item_id UUID NOT NULL REFERENCES %I.question_bank_items(id) ON DELETE CASCADE,
            revision_no INT NOT NULL CHECK (revision_no > 0),
            content TEXT NOT NULL,
            provenance_json JSONB,
            created_by UUID NOT NULL REFERENCES %I.users(id),
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (question_bank_item_id, revision_no)
        )
    $sql$, v_schema, v_schema, v_schema);
    EXECUTE format('ALTER TABLE %I.questions ADD COLUMN IF NOT EXISTS source_bank_revision_id UUID', v_schema);
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE c.conname = 'fk_questions_source_bank_revision'
          AND t.relname = 'questions'
          AND n.nspname = v_schema
    ) THEN
        EXECUTE format(
            'ALTER TABLE %I.questions ADD CONSTRAINT fk_questions_source_bank_revision ' ||
            'FOREIGN KEY (source_bank_revision_id) REFERENCES %I.question_bank_revisions(id)',
            v_schema, v_schema);
    END IF;
    EXECUTE format('CREATE INDEX IF NOT EXISTS ix_questions_source_bank_revision ON %I.questions(source_bank_revision_id)', v_schema);
END;
$$;

COMMIT;
