-- Forward-only upgrade for shared rubric templates and immutable versions.
-- Fresh installs receive the final schema from 01_schema.sql.
-- Run with psql variable app_schema set to the application schema. Never run 01_schema.sql
-- against an existing database.
BEGIN;

SELECT set_config('aives.application_schema', :'app_schema', true);

DO $$
DECLARE
    v_schema text := NULLIF(current_setting('aives.application_schema', true), '');
    v_schema_oid oid;
    v_table_count integer;
    v_trigger_count integer;
BEGIN
    IF v_schema IS NULL THEN
        RAISE EXCEPTION 'Set the required psql variable app_schema to the application schema and retry.';
    END IF;
    IF v_schema = 'information_schema' OR v_schema ~ '^pg_' THEN
        RAISE EXCEPTION 'Schema % is not an application schema.', v_schema;
    END IF;

    SELECT oid INTO v_schema_oid FROM pg_namespace WHERE nspname = v_schema;
    IF v_schema_oid IS NULL THEN
        RAISE EXCEPTION 'Selected application schema % does not exist.', v_schema;
    END IF;

    SELECT count(*) INTO v_table_count
    FROM pg_class
    WHERE relnamespace = v_schema_oid
      AND relkind IN ('r', 'p')
      AND relname IN ('courses', 'users', 'exam_sessions', 'questions', 'rubrics', 'rubric_criteria',
                      'criterion_scores', 'participants', 'interview_attempts', 'question_responses');
    IF v_table_count <> 10 THEN
        RAISE EXCEPTION 'Expected the existing AiOralExam schema tables in %, found % of 10 required tables.',
            v_schema, v_table_count;
    END IF;

    SELECT count(DISTINCT t.tgname) INTO v_trigger_count
    FROM pg_trigger t
    JOIN pg_class c ON c.oid = t.tgrelid
    WHERE c.relnamespace = v_schema_oid
      AND NOT t.tgisinternal
      AND t.tgname IN ('trg_rubrics_require_criteria', 'trg_rubric_criteria_require_parent',
                       'trg_rubric_max_score', 'trg_criterion_score_check', 'trg_response_ai_score');
    IF v_trigger_count <> 5 THEN
        RAISE EXCEPTION 'Expected the five baseline rubric/score triggers in %, found %; inspect the schema before migration.',
            v_schema, v_trigger_count;
    END IF;

    IF EXISTS (
        SELECT 1 FROM pg_class
        WHERE relnamespace = v_schema_oid AND relkind IN ('r', 'p')
          AND relname IN ('rubric_templates', 'rubric_versions')
    ) THEN
        RAISE EXCEPTION 'Rubric version tables already exist in schema %; inspect the schema before retrying.', v_schema;
    END IF;

    PERFORM set_config('search_path', format('%I, pg_catalog', v_schema), true);
END;
$$;

CREATE TABLE rubric_templates (
    id          UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    course_id   UUID         NOT NULL REFERENCES courses(id) ON DELETE RESTRICT,
    name        VARCHAR(200) NOT NULL CHECK (btrim(name) <> ''),
    created_by  UUID         NOT NULL REFERENCES users(id) ON DELETE NO ACTION,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now(),
    UNIQUE (id, course_id)
);
CREATE INDEX ix_rubric_templates_course ON rubric_templates(course_id);

CREATE TABLE rubric_versions (
    id          UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    template_id UUID          NOT NULL,
    course_id   UUID          NOT NULL,
    version_no  INT           NOT NULL CHECK (version_no > 0),
    max_score   NUMERIC(5,2)  NOT NULL DEFAULT 0 CHECK (max_score >= 0),
    is_sealed   BOOLEAN       NOT NULL DEFAULT FALSE,
    created_by  UUID          NOT NULL REFERENCES users(id) ON DELETE NO ACTION,
    created_at  TIMESTAMPTZ   NOT NULL DEFAULT now(),
    CONSTRAINT fk_rubric_versions_template_course
        FOREIGN KEY (template_id, course_id)
        REFERENCES rubric_templates(id, course_id) ON DELETE RESTRICT,
    UNIQUE (template_id, version_no),
    UNIQUE (id, course_id)
);
CREATE INDEX ix_rubric_versions_template ON rubric_versions(template_id, version_no);

ALTER TABLE rubric_criteria ALTER COLUMN rubric_id DROP NOT NULL;
ALTER TABLE rubric_criteria ADD COLUMN rubric_version_id UUID;
ALTER TABLE rubric_criteria
    ADD CONSTRAINT fk_rubric_criteria_version
    FOREIGN KEY (rubric_version_id) REFERENCES rubric_versions(id) ON DELETE RESTRICT;
ALTER TABLE rubric_criteria
    ADD CONSTRAINT ck_rubric_criteria_exactly_one_parent
    CHECK ((rubric_id IS NOT NULL) <> (rubric_version_id IS NOT NULL)) NOT VALID;
ALTER TABLE rubric_criteria VALIDATE CONSTRAINT ck_rubric_criteria_exactly_one_parent;
CREATE UNIQUE INDEX ux_rubric_criteria_version_order
    ON rubric_criteria(rubric_version_id, order_no)
    WHERE rubric_version_id IS NOT NULL;

ALTER TABLE exam_sessions
    ADD COLUMN rubric_mode VARCHAR(20) NOT NULL DEFAULT 'Legacy';
ALTER TABLE exam_sessions ADD COLUMN rubric_version_id UUID;
ALTER TABLE exam_sessions
    ADD CONSTRAINT ck_exam_sessions_rubric_mode
    CHECK (rubric_mode IN ('Legacy', 'SharedVersion'));
ALTER TABLE exam_sessions
    ADD CONSTRAINT fk_exam_sessions_rubric_version_course
    FOREIGN KEY (rubric_version_id, course_id)
    REFERENCES rubric_versions(id, course_id) ON DELETE RESTRICT;
ALTER TABLE exam_sessions
    ADD CONSTRAINT ck_exam_sessions_rubric_configuration
    CHECK (
        (rubric_mode = 'Legacy' AND rubric_version_id IS NULL)
        OR (rubric_mode = 'SharedVersion' AND (rubric_version_id IS NOT NULL OR status = 'Draft'))
    );
ALTER TABLE exam_sessions ALTER COLUMN rubric_mode SET DEFAULT 'Legacy';
CREATE INDEX ix_exam_sessions_rubric_version ON exam_sessions(rubric_version_id)
    WHERE rubric_version_id IS NOT NULL;

CREATE OR REPLACE FUNCTION assert_rubric_version_has_criteria(p_version_id UUID) RETURNS VOID AS $$
DECLARE
    criterion_count BIGINT;
    calculated_max_score NUMERIC;
    stored_max_score NUMERIC(5,2);
    sealed BOOLEAN;
    invalid_description BOOLEAN;
BEGIN
    SELECT is_sealed, max_score INTO sealed, stored_max_score
    FROM rubric_versions WHERE id = p_version_id;
    IF NOT FOUND THEN
        RETURN;
    END IF;
    IF NOT sealed THEN
        RAISE EXCEPTION 'Rubric version % must be sealed before commit.', p_version_id
            USING ERRCODE = '23514';
    END IF;

    SELECT COUNT(*), COALESCE(SUM(max_points), 0), COALESCE(bool_or(btrim(description) = ''), FALSE)
    INTO criterion_count, calculated_max_score, invalid_description
    FROM rubric_criteria WHERE rubric_version_id = p_version_id;
    IF criterion_count = 0 OR invalid_description
       OR calculated_max_score <= 0 OR calculated_max_score > 999.99 THEN
        RAISE EXCEPTION 'Rubric version % must contain valid criteria with a total from 0.01 through 999.99.', p_version_id
            USING ERRCODE = '23514';
    END IF;
    IF stored_max_score IS DISTINCT FROM calculated_max_score THEN
        RAISE EXCEPTION 'Rubric version % max_score does not match its criteria.', p_version_id
            USING ERRCODE = '23514';
    END IF;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;

CREATE OR REPLACE FUNCTION check_rubric_criteria_constraints() RETURNS TRIGGER AS $$
BEGIN
    IF TG_TABLE_NAME = 'rubrics' THEN
        IF TG_OP <> 'DELETE' THEN
            PERFORM assert_rubric_has_criteria(NEW.id);
        END IF;
    ELSIF TG_TABLE_NAME = 'rubric_versions' THEN
        IF TG_OP <> 'DELETE' THEN
            PERFORM assert_rubric_version_has_criteria(NEW.id);
        END IF;
    ELSE
        IF TG_OP <> 'INSERT' AND OLD.rubric_id IS NOT NULL THEN
            PERFORM assert_rubric_has_criteria(OLD.rubric_id);
        END IF;
        IF TG_OP <> 'DELETE' AND NEW.rubric_id IS NOT NULL THEN
            PERFORM assert_rubric_has_criteria(NEW.rubric_id);
        END IF;
        IF TG_OP <> 'INSERT' AND OLD.rubric_version_id IS NOT NULL THEN
            PERFORM assert_rubric_version_has_criteria(OLD.rubric_version_id);
        END IF;
        IF TG_OP <> 'DELETE' AND NEW.rubric_version_id IS NOT NULL THEN
            PERFORM assert_rubric_version_has_criteria(NEW.rubric_version_id);
        END IF;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;

CREATE CONSTRAINT TRIGGER trg_rubric_versions_require_criteria
AFTER INSERT OR UPDATE OR DELETE ON rubric_versions
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION check_rubric_criteria_constraints();

CREATE OR REPLACE FUNCTION fn_sync_rubric_max_score() RETURNS trigger AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        IF OLD.rubric_id IS NOT NULL THEN
            UPDATE rubrics r
               SET max_score = COALESCE((SELECT SUM(c.max_points) FROM rubric_criteria c WHERE c.rubric_id = r.id), 0)
             WHERE r.id = OLD.rubric_id;
        END IF;
        IF OLD.rubric_version_id IS NOT NULL THEN
            UPDATE rubric_versions v
               SET max_score = COALESCE((SELECT SUM(c.max_points) FROM rubric_criteria c WHERE c.rubric_version_id = v.id), 0)
             WHERE v.id = OLD.rubric_version_id;
        END IF;
    END IF;
    IF TG_OP <> 'DELETE' THEN
        IF NEW.rubric_id IS NOT NULL THEN
            UPDATE rubrics r
               SET max_score = COALESCE((SELECT SUM(c.max_points) FROM rubric_criteria c WHERE c.rubric_id = r.id), 0)
             WHERE r.id = NEW.rubric_id;
        END IF;
        IF NEW.rubric_version_id IS NOT NULL THEN
            UPDATE rubric_versions v
               SET max_score = COALESCE((SELECT SUM(c.max_points) FROM rubric_criteria c WHERE c.rubric_version_id = v.id), 0)
             WHERE v.id = NEW.rubric_version_id;
        END IF;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;

CREATE OR REPLACE FUNCTION fn_protect_rubric_version() RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.is_sealed THEN
            RAISE EXCEPTION 'A rubric version must be created unsealed and sealed after its criteria are inserted.'
                USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Rubric versions are append-only and cannot be deleted.'
            USING ERRCODE = '23514';
    END IF;
    IF OLD.is_sealed
       OR (to_jsonb(NEW) - ARRAY['is_sealed', 'max_score']::TEXT[])
          IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['is_sealed', 'max_score']) THEN
        RAISE EXCEPTION 'Rubric versions are immutable; only sealing an unsealed version is allowed.'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;
CREATE TRIGGER trg_rubric_versions_immutable
BEFORE INSERT OR UPDATE OR DELETE ON rubric_versions
FOR EACH ROW EXECUTE FUNCTION fn_protect_rubric_version();

CREATE OR REPLACE FUNCTION fn_reject_rubric_truncate() RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'Rubric versions and criteria cannot be truncated.'
        USING ERRCODE = '23514';
    RETURN NULL;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;
CREATE TRIGGER trg_rubric_versions_no_truncate
BEFORE TRUNCATE ON rubric_versions
FOR EACH STATEMENT EXECUTE FUNCTION fn_reject_rubric_truncate();
CREATE TRIGGER trg_rubric_criteria_no_truncate
BEFORE TRUNCATE ON rubric_criteria
FOR EACH STATEMENT EXECUTE FUNCTION fn_reject_rubric_truncate();

CREATE OR REPLACE FUNCTION fn_protect_sealed_rubric_criteria() RETURNS TRIGGER AS $$
DECLARE
    v_is_sealed BOOLEAN;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.rubric_version_id IS NOT NULL THEN
            SELECT is_sealed INTO v_is_sealed
            FROM rubric_versions WHERE id = NEW.rubric_version_id FOR UPDATE;
            IF NOT FOUND OR v_is_sealed THEN
                RAISE EXCEPTION 'Criteria can only be inserted into an unsealed rubric version.'
                    USING ERRCODE = '23514';
            END IF;
        END IF;
        RETURN NEW;
    END IF;

    IF OLD.rubric_version_id IS NOT NULL
       OR (TG_OP = 'UPDATE' AND NEW.rubric_version_id IS NOT NULL) THEN
        RAISE EXCEPTION 'Criteria belonging to a rubric version are append-only.'
            USING ERRCODE = '23514';
    END IF;

    IF TG_OP = 'UPDATE' THEN
        RETURN NEW;
    END IF;
    RETURN OLD;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;
CREATE TRIGGER trg_rubric_criteria_immutable
BEFORE INSERT OR UPDATE OR DELETE ON rubric_criteria
FOR EACH ROW EXECUTE FUNCTION fn_protect_sealed_rubric_criteria();

CREATE OR REPLACE FUNCTION fn_check_exam_session_rubric() RETURNS TRIGGER AS $$
DECLARE
    v_sealed BOOLEAN;
BEGIN
    IF NEW.rubric_mode = 'Legacy' AND NEW.rubric_version_id IS NOT NULL THEN
        RAISE EXCEPTION 'Legacy sessions cannot reference a shared rubric version.'
            USING ERRCODE = '23514';
    END IF;
    IF NEW.rubric_version_id IS NOT NULL THEN
        SELECT is_sealed INTO v_sealed FROM rubric_versions
        WHERE id = NEW.rubric_version_id AND course_id = NEW.course_id;
        IF NOT FOUND OR NOT v_sealed THEN
            RAISE EXCEPTION 'A session can select only a sealed rubric version owned by its course.'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NEW.rubric_mode = 'SharedVersion' AND EXISTS (
        SELECT 1
        FROM questions q
        JOIN rubrics r ON r.question_id = q.id
        WHERE q.exam_session_id = NEW.id
    ) THEN
        RAISE EXCEPTION 'A shared-version session cannot contain question-specific rubrics.'
            USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.rubric_mode IS DISTINCT FROM OLD.rubric_mode THEN
        RAISE EXCEPTION 'A session cannot change between Legacy and SharedVersion modes.'
            USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE'
       AND NEW.rubric_version_id IS DISTINCT FROM OLD.rubric_version_id
       AND EXISTS (SELECT 1 FROM questions WHERE exam_session_id = OLD.id) THEN
        RAISE EXCEPTION 'A Draft session rubric version cannot change after questions have been added.'
            USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status <> 'Draft'
       AND (NEW.rubric_version_id IS DISTINCT FROM OLD.rubric_version_id
            OR NEW.status = 'Draft') THEN
        RAISE EXCEPTION 'A session rubric configuration cannot change after the session leaves Draft.'
            USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status = 'Closed' AND NEW.status <> 'Closed' THEN
        RAISE EXCEPTION 'A closed session cannot be reopened.'
            USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status = 'Published' AND NEW.status NOT IN ('Published', 'Closed') THEN
        RAISE EXCEPTION 'A published session can only be closed.'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;
CREATE TRIGGER trg_exam_sessions_rubric_configuration
BEFORE INSERT OR UPDATE ON exam_sessions
FOR EACH ROW EXECUTE FUNCTION fn_check_exam_session_rubric();

CREATE OR REPLACE FUNCTION fn_protect_published_shared_session_delete() RETURNS TRIGGER AS $$
BEGIN
    IF OLD.rubric_mode = 'SharedVersion' AND OLD.status <> 'Draft' THEN
        RAISE EXCEPTION 'Published shared sessions and their historical data cannot be deleted.'
            USING ERRCODE = '23514';
    END IF;
    RETURN OLD;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;
CREATE TRIGGER trg_exam_sessions_published_shared_no_delete
BEFORE DELETE ON exam_sessions
FOR EACH ROW EXECUTE FUNCTION fn_protect_published_shared_session_delete();

CREATE OR REPLACE FUNCTION fn_reject_published_shared_truncate() RETURNS TRIGGER AS $$
BEGIN
    IF TG_TABLE_NAME = 'exam_sessions' AND EXISTS (
        SELECT 1 FROM exam_sessions
        WHERE rubric_mode = 'SharedVersion' AND status <> 'Draft'
    ) THEN
        RAISE EXCEPTION 'Published shared sessions cannot be truncated.'
            USING ERRCODE = '23514';
    ELSIF TG_TABLE_NAME = 'questions' AND EXISTS (
        SELECT 1 FROM questions q
        JOIN exam_sessions s ON s.id = q.exam_session_id
        WHERE s.rubric_mode = 'SharedVersion' AND s.status <> 'Draft'
    ) THEN
        RAISE EXCEPTION 'Questions in published shared sessions cannot be truncated.'
            USING ERRCODE = '23514';
    ELSIF TG_TABLE_NAME = 'participants' AND EXISTS (
        SELECT 1 FROM participants p
        JOIN exam_sessions s ON s.id = p.exam_session_id
        WHERE s.rubric_mode = 'SharedVersion' AND s.status <> 'Draft'
    ) THEN
        RAISE EXCEPTION 'Participants in published shared sessions cannot be truncated.'
            USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;
CREATE TRIGGER trg_exam_sessions_published_shared_no_truncate
BEFORE TRUNCATE ON exam_sessions
FOR EACH STATEMENT EXECUTE FUNCTION fn_reject_published_shared_truncate();
CREATE TRIGGER trg_questions_published_shared_no_truncate
BEFORE TRUNCATE ON questions
FOR EACH STATEMENT EXECUTE FUNCTION fn_reject_published_shared_truncate();
CREATE TRIGGER trg_participants_published_shared_no_truncate
BEFORE TRUNCATE ON participants
FOR EACH STATEMENT EXECUTE FUNCTION fn_reject_published_shared_truncate();

CREATE OR REPLACE FUNCTION fn_assert_published_shared_session() RETURNS TRIGGER AS $$
DECLARE
    v_session_id UUID;
    v_mode VARCHAR(20);
    v_status VARCHAR(20);
    v_version_id UUID;
    v_version_max NUMERIC(5,2);
    v_question_count BIGINT;
    v_participant_count BIGINT;
BEGIN
    v_session_id := CASE WHEN TG_OP = 'DELETE' THEN OLD.id ELSE NEW.id END;
    SELECT rubric_mode, status, rubric_version_id
    INTO v_mode, v_status, v_version_id
    FROM exam_sessions WHERE id = v_session_id;
    IF NOT FOUND OR v_mode <> 'SharedVersion' OR v_status = 'Draft' THEN
        RETURN NULL;
    END IF;
    SELECT max_score INTO v_version_max
    FROM rubric_versions WHERE id = v_version_id AND is_sealed;
    IF v_version_max IS NULL THEN
        RAISE EXCEPTION 'Published shared session % requires a sealed rubric version.', v_session_id
            USING ERRCODE = '23514';
    END IF;
    SELECT COUNT(*) INTO v_question_count FROM questions WHERE exam_session_id = v_session_id;
    SELECT COUNT(*) INTO v_participant_count FROM participants WHERE exam_session_id = v_session_id;
    IF v_question_count = 0 OR v_participant_count = 0 THEN
        RAISE EXCEPTION 'Published shared session % requires at least one question and participant.', v_session_id
            USING ERRCODE = '23514';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM questions q
        JOIN rubrics r ON r.question_id = q.id
        WHERE q.exam_session_id = v_session_id
    ) THEN
        RAISE EXCEPTION 'Published shared session % cannot contain question-specific rubrics.', v_session_id
            USING ERRCODE = '23514';
    END IF;
    IF v_question_count * v_version_max > 999.99 THEN
        RAISE EXCEPTION 'Published shared session % maximum score cannot exceed 999.99.', v_session_id
            USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;
CREATE CONSTRAINT TRIGGER trg_exam_sessions_shared_publish_valid
AFTER INSERT OR UPDATE ON exam_sessions
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION fn_assert_published_shared_session();

CREATE OR REPLACE FUNCTION fn_protect_published_shared_questions() RETURNS TRIGGER AS $$
DECLARE
    v_session_id UUID;
    v_status VARCHAR(20);
    v_mode VARCHAR(20);
    v_version_id UUID;
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.exam_session_id IS DISTINCT FROM NEW.exam_session_id THEN
        PERFORM id
        FROM exam_sessions
        WHERE id IN (OLD.exam_session_id, NEW.exam_session_id)
        ORDER BY id
        FOR UPDATE;
    END IF;

    IF TG_OP <> 'INSERT' THEN
        v_session_id := OLD.exam_session_id;
        SELECT status, rubric_mode, rubric_version_id INTO v_status, v_mode, v_version_id
        FROM exam_sessions WHERE id = v_session_id FOR UPDATE;
        IF v_mode = 'SharedVersion' AND v_status <> 'Draft' THEN
            RAISE EXCEPTION 'Questions in a published shared session are immutable.'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    IF TG_OP <> 'DELETE' THEN
        v_session_id := NEW.exam_session_id;
        SELECT status, rubric_mode, rubric_version_id INTO v_status, v_mode, v_version_id
        FROM exam_sessions WHERE id = v_session_id FOR UPDATE;
        IF v_mode = 'SharedVersion' AND v_status <> 'Draft' THEN
            RAISE EXCEPTION 'Questions in a published shared session are immutable.'
                USING ERRCODE = '23514';
        END IF;
        IF v_mode = 'SharedVersion' AND v_version_id IS NULL THEN
            RAISE EXCEPTION 'Select a sealed rubric version before adding questions to a shared session.'
                USING ERRCODE = '23514';
        END IF;
        IF v_mode = 'SharedVersion' AND EXISTS (SELECT 1 FROM rubrics WHERE question_id = NEW.id) THEN
            RAISE EXCEPTION 'A shared-version session cannot use question-specific rubrics.'
                USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    RETURN OLD;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;
CREATE TRIGGER trg_questions_published_shared_immutable
BEFORE INSERT OR UPDATE OR DELETE ON questions
FOR EACH ROW EXECUTE FUNCTION fn_protect_published_shared_questions();

CREATE OR REPLACE FUNCTION fn_reject_legacy_rubric_for_shared_session() RETURNS TRIGGER AS $$
DECLARE
    v_rubric_mode VARCHAR(20);
BEGIN
    SELECT s.rubric_mode INTO v_rubric_mode
    FROM questions q
    JOIN exam_sessions s ON s.id = q.exam_session_id
    WHERE q.id = NEW.question_id
    FOR UPDATE OF s;
    IF v_rubric_mode = 'SharedVersion' THEN
        RAISE EXCEPTION 'A shared-version session cannot use question-specific rubrics.'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;
CREATE TRIGGER trg_rubrics_reject_shared_session
BEFORE INSERT OR UPDATE ON rubrics
FOR EACH ROW EXECUTE FUNCTION fn_reject_legacy_rubric_for_shared_session();

CREATE OR REPLACE FUNCTION fn_protect_published_shared_participants() RETURNS TRIGGER AS $$
DECLARE
    v_mode VARCHAR(20);
    v_status VARCHAR(20);
BEGIN
    SELECT rubric_mode, status INTO v_mode, v_status
    FROM exam_sessions WHERE id = OLD.exam_session_id FOR UPDATE;
    IF v_mode = 'SharedVersion' AND v_status <> 'Draft' THEN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Participants in a published shared session cannot be deleted or reassigned.'
                USING ERRCODE = '23514';
        ELSIF NEW.exam_session_id IS DISTINCT FROM OLD.exam_session_id THEN
            RAISE EXCEPTION 'Participants in a published shared session cannot be deleted or reassigned.'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;
CREATE TRIGGER trg_participants_published_shared_protection
BEFORE UPDATE OR DELETE ON participants
FOR EACH ROW EXECUTE FUNCTION fn_protect_published_shared_participants();

CREATE OR REPLACE FUNCTION fn_check_criterion_score() RETURNS trigger AS $$
DECLARE
    v_max NUMERIC(5,2);
BEGIN
    SELECT c.max_points INTO v_max
    FROM rubric_criteria c
    JOIN rubrics r ON r.id = c.rubric_id
    JOIN question_responses qr ON qr.question_id = r.question_id
    JOIN interview_attempts ia ON ia.id = qr.attempt_id
    JOIN participants p ON p.id = ia.participant_id
    JOIN exam_sessions s ON s.id = p.exam_session_id
    WHERE c.id = NEW.rubric_criterion_id
      AND qr.id = NEW.question_response_id
      AND c.rubric_version_id IS NULL
      AND s.rubric_mode = 'Legacy';

    IF NOT FOUND THEN
        SELECT c.max_points INTO v_max
        FROM rubric_criteria c
        JOIN question_responses qr ON qr.id = NEW.question_response_id
        JOIN interview_attempts ia ON ia.id = qr.attempt_id
        JOIN participants p ON p.id = ia.participant_id
        JOIN exam_sessions s ON s.id = p.exam_session_id
        JOIN questions q ON q.id = qr.question_id AND q.exam_session_id = s.id
        WHERE c.id = NEW.rubric_criterion_id
          AND c.rubric_id IS NULL
          AND c.rubric_version_id = s.rubric_version_id
          AND s.rubric_mode = 'SharedVersion';
    END IF;
    IF v_max IS NULL THEN
        RAISE EXCEPTION 'Criterion % does not belong to the rubric configuration of response %',
            NEW.rubric_criterion_id, NEW.question_response_id;
    END IF;
    IF NEW.points > v_max THEN
        RAISE EXCEPTION 'Criterion score % exceeds max_points %', NEW.points, v_max;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;

COMMIT;
