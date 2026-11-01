-- Forward-only upgrade for platform-owned shared rubric templates and active versions.
-- Preserves existing course-scoped versions and all session pins. Do not run against existing
-- databases without the approved manual change process; run with psql variable app_schema.
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
    IF v_schema = 'information_schema' OR v_schema ~ '^pg_' THEN
        RAISE EXCEPTION 'Schema % is not an application schema.', v_schema;
    END IF;
    SELECT oid INTO v_schema_oid FROM pg_namespace WHERE nspname = v_schema;
    IF v_schema_oid IS NULL THEN
        RAISE EXCEPTION 'Selected application schema % does not exist.', v_schema;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_class WHERE relnamespace = v_schema_oid AND relname = 'rubric_versions' AND relkind IN ('r', 'p'))
       OR NOT EXISTS (SELECT 1 FROM pg_class WHERE relnamespace = v_schema_oid AND relname = 'rubric_templates' AND relkind IN ('r', 'p')) THEN
        RAISE EXCEPTION 'Expected rubric_templates and rubric_versions in application schema %.', v_schema;
    END IF;
    PERFORM set_config('search_path', format('%I, pg_catalog', v_schema), true);
END;
$$;

ALTER TABLE rubric_templates ALTER COLUMN course_id DROP NOT NULL;
ALTER TABLE rubric_versions ALTER COLUMN course_id DROP NOT NULL;
ALTER TABLE rubric_versions ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE rubric_versions DROP CONSTRAINT IF EXISTS fk_rubric_versions_template_course;
ALTER TABLE rubric_versions DROP CONSTRAINT IF EXISTS fk_rubric_versions_template;
ALTER TABLE rubric_versions
    ADD CONSTRAINT fk_rubric_versions_template
    FOREIGN KEY (template_id) REFERENCES rubric_templates(id) ON DELETE RESTRICT;

ALTER TABLE exam_sessions DROP CONSTRAINT IF EXISTS fk_exam_sessions_rubric_version_course;
ALTER TABLE exam_sessions DROP CONSTRAINT IF EXISTS fk_exam_sessions_rubric_version;
ALTER TABLE exam_sessions
    ADD CONSTRAINT fk_exam_sessions_rubric_version
    FOREIGN KEY (rubric_version_id) REFERENCES rubric_versions(id) ON DELETE RESTRICT;

DROP INDEX IF EXISTS ux_rubric_versions_active_template;

WITH ranked_active_versions AS (
    SELECT id, row_number() OVER (ORDER BY created_at, id) AS active_order
    FROM rubric_versions
    WHERE is_active
)
UPDATE rubric_versions v
SET is_active = FALSE
FROM ranked_active_versions ranked
WHERE v.id = ranked.id AND ranked.active_order > 1;

CREATE UNIQUE INDEX IF NOT EXISTS ux_rubric_versions_single_active
    ON rubric_versions(is_active) WHERE is_active;

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
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fn_check_exam_session_rubric() RETURNS TRIGGER AS $$
DECLARE
    v_sealed BOOLEAN;
    v_active BOOLEAN;
    v_version_course_id UUID;
BEGIN
    IF NEW.rubric_mode = 'Legacy' AND NEW.rubric_version_id IS NOT NULL THEN
        RAISE EXCEPTION 'Legacy sessions cannot reference a shared rubric version.'
            USING ERRCODE = '23514';
    END IF;
    IF NEW.rubric_version_id IS NOT NULL THEN
        SELECT is_sealed, is_active, course_id INTO v_sealed, v_active, v_version_course_id
        FROM rubric_versions WHERE id = NEW.rubric_version_id;
        IF NOT FOUND OR NOT v_sealed THEN
            RAISE EXCEPTION 'A session can select only a sealed rubric version.'
                USING ERRCODE = '23514';
        END IF;
        IF NEW.status = 'Draft' AND (NOT v_active OR v_version_course_id IS NOT NULL) THEN
            IF TG_OP = 'INSERT' THEN
                RAISE EXCEPTION 'A new shared session must pin an active platform rubric version.'
                    USING ERRCODE = '23514';
            ELSIF NEW.rubric_version_id IS DISTINCT FROM OLD.rubric_version_id THEN
                RAISE EXCEPTION 'A new shared session must pin an active platform rubric version.'
                    USING ERRCODE = '23514';
            END IF;
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
$$ LANGUAGE plpgsql;

-- Seed one immutable platform-owned rubric version only when an administrator is present.
INSERT INTO rubric_templates (id, course_id, name, created_by)
SELECT 'aaaaaaaa-0000-0000-0000-000000000001', NULL, 'Platform Shared Oral Rubric', u.id
FROM users u JOIN roles r ON r.id = u.role_id
WHERE r.name = 'Administrator'
ORDER BY u.id LIMIT 1
ON CONFLICT (id) DO NOTHING;

INSERT INTO rubric_versions (id, template_id, course_id, version_no, max_score, is_sealed, is_active, created_by)
SELECT 'aaaaaaaa-0000-0000-0000-000000000002', t.id, NULL, 1, 10.00, FALSE, FALSE, t.created_by
FROM rubric_templates t WHERE t.id = 'aaaaaaaa-0000-0000-0000-000000000001'
ON CONFLICT (id) DO NOTHING;

INSERT INTO rubric_criteria (id, rubric_id, rubric_version_id, order_no, description, max_points)
VALUES
    ('aaaaaaaa-0000-0000-0000-000000000003', NULL, 'aaaaaaaa-0000-0000-0000-000000000002', 1, 'Accuracy and correctness', 5.00),
    ('aaaaaaaa-0000-0000-0000-000000000004', NULL, 'aaaaaaaa-0000-0000-0000-000000000002', 2, 'Clarity and completeness', 5.00)
ON CONFLICT (id) DO NOTHING;

UPDATE rubric_versions
SET is_sealed = TRUE, max_score = 10.00
WHERE id = 'aaaaaaaa-0000-0000-0000-000000000002' AND NOT is_sealed;

UPDATE rubric_versions v
SET is_active = TRUE
WHERE v.id = 'aaaaaaaa-0000-0000-0000-000000000002'
  AND NOT EXISTS (SELECT 1 FROM rubric_versions active WHERE active.is_active);

COMMIT;
