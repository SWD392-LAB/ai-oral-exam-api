-- Forward-only fix for sealed rubric activation and SharedVersion session publishing.
-- Historical rows are preserved; existing sessions retain their rubric_version_id.
-- Run manually with psql variable app_schema set to the application schema.
BEGIN;

SELECT set_config('aives.application_schema', :'app_schema', true);

DO $$
DECLARE
    v_schema text := NULLIF(current_setting('aives.application_schema', true), '');
    v_schema_oid oid;
BEGIN
    IF v_schema IS NULL THEN
        RAISE EXCEPTION 'Set the required psql variable app_schema and retry.';
    END IF;
    IF v_schema IN ('public', 'information_schema') OR v_schema ~ '^pg_' THEN
        RAISE EXCEPTION 'Schema % is not an application schema.', v_schema;
    END IF;

    SELECT oid INTO v_schema_oid FROM pg_namespace WHERE nspname = v_schema;
    IF v_schema_oid IS NULL THEN
        RAISE EXCEPTION 'Selected application schema % does not exist.', v_schema;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_class
        WHERE relnamespace = v_schema_oid AND relname = 'rubric_versions' AND relkind IN ('r', 'p')
    ) OR NOT EXISTS (
        SELECT 1 FROM pg_class
        WHERE relnamespace = v_schema_oid AND relname = 'rubric_templates' AND relkind IN ('r', 'p')
    ) OR NOT EXISTS (
        SELECT 1 FROM pg_class
        WHERE relnamespace = v_schema_oid AND relname = 'exam_sessions' AND relkind IN ('r', 'p')
    ) THEN
        RAISE EXCEPTION 'Expected rubric_versions, rubric_templates, and exam_sessions in schema %.', v_schema;
    END IF;

    PERFORM set_config('search_path', format('%I, pg_catalog', v_schema), true);
END;
$$;

ALTER TABLE exam_sessions ALTER COLUMN rubric_mode SET DEFAULT 'SharedVersion';

CREATE OR REPLACE FUNCTION fn_lock_rubric_activation() RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(5067184210567320137);
    RETURN NULL;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;

DROP TRIGGER IF EXISTS trg_rubric_versions_active_lock ON rubric_versions;
CREATE TRIGGER trg_rubric_versions_active_lock
BEFORE UPDATE OF is_active ON rubric_versions
FOR EACH STATEMENT EXECUTE FUNCTION fn_lock_rubric_activation();

CREATE OR REPLACE FUNCTION fn_protect_rubric_version() RETURNS TRIGGER AS $$
DECLARE
    v_template_course_id UUID;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.is_sealed OR NEW.is_active THEN
            RAISE EXCEPTION 'A rubric version must be created unsealed and inactive before it is sealed and activated.'
                USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Rubric versions are append-only and cannot be deleted.'
            USING ERRCODE = '23514';
    END IF;
    IF OLD.is_sealed
       AND (to_jsonb(NEW) - ARRAY['is_active']::TEXT[])
           IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['is_active']::TEXT[]) THEN
        RAISE EXCEPTION 'A sealed rubric version is immutable except for its active state.'
            USING ERRCODE = '23514';
    END IF;
    IF NOT OLD.is_sealed
       AND (to_jsonb(NEW) - ARRAY['is_sealed', 'max_score']::TEXT[])
           IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['is_sealed', 'max_score']::TEXT[]) THEN
        RAISE EXCEPTION 'Rubric versions are immutable; only sealing an unsealed version is allowed.'
            USING ERRCODE = '23514';
    END IF;
    IF NEW.is_active THEN
        SELECT course_id INTO v_template_course_id
        FROM rubric_templates WHERE id = NEW.template_id;
        IF v_template_course_id IS NOT NULL OR NEW.course_id IS NOT NULL THEN
            RAISE EXCEPTION 'Only platform-owned rubric versions can be activated.'
                USING ERRCODE = '23514';
        END IF;
        IF NOT NEW.is_sealed THEN
            RAISE EXCEPTION 'Only sealed rubric versions can be activated.'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;

CREATE OR REPLACE FUNCTION fn_check_exam_session_rubric() RETURNS TRIGGER AS $$
DECLARE
    v_sealed BOOLEAN;
    v_active BOOLEAN;
    v_version_course_id UUID;
    v_template_course_id UUID;
    v_initial_publish BOOLEAN;
BEGIN
    IF TG_OP = 'INSERT' THEN
        v_initial_publish := NEW.status = 'Published';
    ELSE
        v_initial_publish := OLD.status = 'Draft' AND NEW.status = 'Published';
    END IF;

    IF NEW.rubric_mode = 'Legacy' AND NEW.rubric_version_id IS NOT NULL THEN
        RAISE EXCEPTION 'Legacy sessions cannot reference a shared rubric version.'
            USING ERRCODE = '23514';
    END IF;

    IF NEW.rubric_mode = 'SharedVersion' THEN
        IF TG_OP = 'INSERT' THEN
            PERFORM pg_advisory_xact_lock(5067184210567320137);
        ELSIF NEW.rubric_version_id IS DISTINCT FROM OLD.rubric_version_id
           OR v_initial_publish THEN
            PERFORM pg_advisory_xact_lock(5067184210567320137);
        END IF;
    END IF;

    IF NEW.rubric_version_id IS NOT NULL THEN
        SELECT v.is_sealed, v.is_active, v.course_id, t.course_id
        INTO v_sealed, v_active, v_version_course_id, v_template_course_id
        FROM rubric_versions v
        JOIN rubric_templates t ON t.id = v.template_id
        WHERE v.id = NEW.rubric_version_id;
        IF NOT FOUND OR NOT v_sealed THEN
            RAISE EXCEPTION 'A session can select only a sealed rubric version.'
                USING ERRCODE = '23514';
        END IF;
        IF NEW.status = 'Draft'
           AND (NOT v_active OR v_version_course_id IS NOT NULL OR v_template_course_id IS NOT NULL) THEN
            IF TG_OP = 'INSERT' THEN
                RAISE EXCEPTION 'A new shared session must pin an active platform rubric version.'
                    USING ERRCODE = '23514';
            ELSIF NEW.rubric_version_id IS DISTINCT FROM OLD.rubric_version_id THEN
                RAISE EXCEPTION 'A new shared session must pin an active platform rubric version.'
                    USING ERRCODE = '23514';
            END IF;
        END IF;
        IF NEW.rubric_mode = 'SharedVersion' AND v_initial_publish
           AND (NOT v_active OR v_version_course_id IS NOT NULL OR v_template_course_id IS NOT NULL) THEN
            RAISE EXCEPTION 'A published shared session must pin an active platform rubric version.'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NEW.rubric_mode = 'SharedVersion' AND EXISTS (
        SELECT 1 FROM questions q JOIN rubrics r ON r.question_id = q.id WHERE q.exam_session_id = NEW.id
    ) THEN
        RAISE EXCEPTION 'A shared-version session cannot contain question-specific rubrics.'
            USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.rubric_mode IS DISTINCT FROM OLD.rubric_mode THEN
        RAISE EXCEPTION 'A session cannot change between Legacy and SharedVersion modes.' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.rubric_version_id IS DISTINCT FROM OLD.rubric_version_id
       AND EXISTS (SELECT 1 FROM questions WHERE exam_session_id = OLD.id) THEN
        RAISE EXCEPTION 'A Draft session rubric version cannot change after questions have been added.' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status <> 'Draft'
       AND (NEW.rubric_version_id IS DISTINCT FROM OLD.rubric_version_id OR NEW.status = 'Draft') THEN
        RAISE EXCEPTION 'A session rubric configuration cannot change after the session leaves Draft.' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status = 'Closed' AND NEW.status <> 'Closed' THEN
        RAISE EXCEPTION 'A closed session cannot be reopened.' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status = 'Published' AND NEW.status NOT IN ('Published', 'Closed') THEN
        RAISE EXCEPTION 'A published session can only be closed.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;

COMMIT;
