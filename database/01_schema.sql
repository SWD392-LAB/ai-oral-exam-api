-- =====================================================================
-- AIVES - AI-powered Viva Exam System
-- 01_schema.sql : creates the tables for PostgreSQL (>= 13)
-- Follows docs/ConceptualERD.drawio + docs/ClassDiagram.drawio, with the
-- F1 question-bank/material tables and the course_lecturers join table added.
-- Enums are stored as VARCHAR + CHECK so they map easily to EF Core (string conversion).
-- Derived values (rubrics.max_score, question_responses.ai_suggested_score) are kept
-- in sync by triggers at the end of this file.
-- =====================================================================

-- Re-runnable: drop old tables first (old + new names, reverse dependency order)
DROP TABLE IF EXISTS criterion_scores, score_reviews, ai_evaluations, interview_turns,
    question_responses, interview_attempts, participants, rubric_criteria, rubrics,
    questions, material_chunks, course_materials, question_bank_revisions,
    question_bank_items, exam_sessions, course_lecturers, courses, ai_service_configs, audit_logs,
    email_link_tokens, password_setup_tokens, user_accounts, users, roles CASCADE;

-- ---------------------------------------------------------------------
-- F7 - Access Control
-- ---------------------------------------------------------------------

-- ERD: ROLE  (Class diagram: Role)
CREATE TABLE roles (
    id           UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    name         VARCHAR(20)  NOT NULL UNIQUE
                 CHECK (name IN ('Student', 'Lecturer', 'Administrator')),
    description  VARCHAR(255)
);

-- ERD: USER  (ROLE 1 --< 0..* "has") - the person; sign-in is handled by Keycloak
-- id = Keycloak user id (token sub); role mirrors the Keycloak realm role
CREATE TABLE users (
    id             UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    role_id        UUID         NOT NULL REFERENCES roles(id),
    full_name      VARCHAR(150) NOT NULL,
    email          VARCHAR(255) NOT NULL,
    username       VARCHAR(50)  NOT NULL UNIQUE,  -- unique sign-in name = the Keycloak username
    student_code   VARCHAR(20)  UNIQUE,     -- Student only
    lecturer_code  VARCHAR(20)  UNIQUE,     -- Lecturer only
    class_code     VARCHAR(20),             -- Student only, e.g. SE1801
    is_active      BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at     TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE OR REPLACE FUNCTION fn_users_check_role_codes() RETURNS TRIGGER AS $$
DECLARE
    v_role_name roles.name%TYPE;
BEGIN
    SELECT r.name INTO v_role_name
    FROM roles r
    WHERE r.id = NEW.role_id;

    IF NOT FOUND THEN
        RETURN NEW;
    END IF;

    IF v_role_name = 'Student' THEN
        IF NEW.student_code IS NULL OR btrim(NEW.student_code) = '' OR NEW.lecturer_code IS NOT NULL THEN
            RAISE EXCEPTION 'Student user % must have a non-blank student_code and no lecturer_code.', NEW.id
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_users_role_codes';
        END IF;
    ELSIF v_role_name = 'Lecturer' THEN
        IF NEW.lecturer_code IS NULL OR btrim(NEW.lecturer_code) = '' OR NEW.student_code IS NOT NULL THEN
            RAISE EXCEPTION 'Lecturer user % must have a non-blank lecturer_code and no student_code.', NEW.id
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_users_role_codes';
        END IF;
    ELSIF v_role_name = 'Administrator' THEN
        IF NEW.student_code IS NOT NULL OR NEW.lecturer_code IS NOT NULL THEN
            RAISE EXCEPTION 'Administrator user % must not have student_code or lecturer_code.', NEW.id
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_users_role_codes';
        END IF;
    ELSE
        RAISE EXCEPTION 'User % has an unsupported role for role-code validation.', NEW.id
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_users_role_codes';
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER ck_users_role_codes
BEFORE INSERT OR UPDATE ON users
FOR EACH ROW EXECUTE FUNCTION fn_users_check_role_codes();

CREATE INDEX ix_users_role ON users(role_id);
CREATE UNIQUE INDEX ux_users_email ON users (lower(email));

-- ERD: AUDIT LOG  (USER 1 --< 0..* "performs")
CREATE TABLE audit_logs (
    id             UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id        UUID         NOT NULL REFERENCES users(id),
    action         VARCHAR(100) NOT NULL,
    target_entity  VARCHAR(100) NOT NULL,
    target_id      UUID,                    -- not in the class diagram; kept so a log points at the exact row
    description    VARCHAR(500),
    created_at     TIMESTAMPTZ  NOT NULL DEFAULT now()
);
CREATE INDEX ix_audit_user ON audit_logs(user_id);
CREATE INDEX ix_audit_created ON audit_logs(created_at DESC);

-- ERD: AI SERVICE CONFIG  (USER 1 --< 0..* "configures")
CREATE TABLE ai_service_configs (
    id                 UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    configured_by      UUID         NOT NULL REFERENCES users(id),
    service_type       VARCHAR(10)  NOT NULL CHECK (service_type IN ('STT', 'TTS', 'LLM')),
    provider_name      VARCHAR(100) NOT NULL,
    endpoint_url       VARCHAR(500) NOT NULL,
    api_key_encrypted  TEXT         NOT NULL,
    language           VARCHAR(20)  NOT NULL DEFAULT 'Vietnamese'
                       CHECK (language IN ('Vietnamese', 'English')),
    is_active          BOOLEAN      NOT NULL DEFAULT TRUE,
    updated_at         TIMESTAMPTZ  NOT NULL DEFAULT now()   -- not in the class diagram; technical column
);
-- Only one active configuration per service type
CREATE UNIQUE INDEX ux_ai_config_active ON ai_service_configs(service_type) WHERE is_active;

-- ---------------------------------------------------------------------
-- F7 - Exam Configuration
-- ---------------------------------------------------------------------

-- ERD: COURSE
CREATE TABLE courses (
    id    UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    code  VARCHAR(20)  NOT NULL UNIQUE,
    name  VARCHAR(200) NOT NULL
);

CREATE TABLE rubric_templates (
    id          UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    course_id   UUID         REFERENCES courses(id) ON DELETE RESTRICT,
    name        VARCHAR(200) NOT NULL CHECK (btrim(name) <> ''),
    created_by  UUID         NOT NULL REFERENCES users(id) ON DELETE NO ACTION,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now(),
    UNIQUE (id, course_id)
);
CREATE INDEX ix_rubric_templates_course ON rubric_templates(course_id);

CREATE TABLE rubric_versions (
    id          UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    template_id UUID          NOT NULL REFERENCES rubric_templates(id) ON DELETE RESTRICT,
    course_id   UUID,
    version_no  INT           NOT NULL CHECK (version_no > 0),
    max_score   NUMERIC(5,2)  NOT NULL DEFAULT 0 CHECK (max_score >= 0),
    is_sealed   BOOLEAN       NOT NULL DEFAULT FALSE,
    is_active   BOOLEAN       NOT NULL DEFAULT FALSE,
    created_by  UUID          NOT NULL REFERENCES users(id) ON DELETE NO ACTION,
    created_at  TIMESTAMPTZ   NOT NULL DEFAULT now(),
    UNIQUE (template_id, version_no),
    UNIQUE (id, course_id)
);
CREATE INDEX ix_rubric_versions_template ON rubric_versions(template_id, version_no);
CREATE UNIQUE INDEX ux_rubric_versions_single_active ON rubric_versions(is_active) WHERE is_active;

-- ERD: COURSE >o--|< USER "teaches"  (N-N -> join table)
-- The diagram says every course has 1..* lecturers; a DB cannot enforce "at least one",
-- so the application must assign a lecturer when it creates a course.
CREATE TABLE course_lecturers (
    course_id    UUID        NOT NULL REFERENCES courses(id) ON DELETE CASCADE,
    lecturer_id  UUID        NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    assigned_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (course_id, lecturer_id)
);
CREATE INDEX ix_course_lecturers_lecturer ON course_lecturers(lecturer_id);

-- F1 - Question bank (rubric is intentionally not stored on bank items)
CREATE TABLE question_bank_items (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    course_id   UUID NOT NULL REFERENCES courses(id) ON DELETE CASCADE,
    topic       VARCHAR(200) NOT NULL,
    bloom       VARCHAR(30) NOT NULL CHECK (bloom IN ('Remember', 'Understand', 'Apply', 'Analyze')),
    content     TEXT NOT NULL,
    origin      VARCHAR(20) NOT NULL CHECK (origin IN ('Manual', 'Import', 'Ai')),
    status      VARCHAR(20) NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft', 'Approved', 'Rejected', 'Archived')),
    created_by  UUID NOT NULL REFERENCES users(id),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ix_question_bank_course_status ON question_bank_items(course_id, status);

CREATE TABLE question_bank_revisions (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    question_bank_item_id UUID NOT NULL REFERENCES question_bank_items(id) ON DELETE CASCADE,
    revision_no           INT NOT NULL CHECK (revision_no > 0),
    content               TEXT NOT NULL,
    provenance_json       JSONB,
    created_by            UUID NOT NULL REFERENCES users(id),
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (question_bank_item_id, revision_no)
);

CREATE TABLE course_materials (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    course_id     UUID NOT NULL REFERENCES courses(id) ON DELETE CASCADE,
    file_name     VARCHAR(255) NOT NULL,
    content_type  VARCHAR(100) NOT NULL,
    size_bytes    BIGINT NOT NULL CHECK (size_bytes > 0),
    sha256        VARCHAR(64) NOT NULL,
    storage_key   VARCHAR(500) NOT NULL,
    status        VARCHAR(20) NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Ready', 'Failed', 'Archived')),
    ingest_error  VARCHAR(500),
    created_by    UUID NOT NULL REFERENCES users(id),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ix_course_materials_course_status ON course_materials(course_id, status);

CREATE TABLE material_chunks (
    id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    course_material_id UUID NOT NULL REFERENCES course_materials(id) ON DELETE CASCADE,
    chunk_no           INT NOT NULL CHECK (chunk_no > 0),
    citation           VARCHAR(300) NOT NULL,
    text               TEXT NOT NULL,
    UNIQUE (course_material_id, chunk_no)
);

-- ERD: EXAM SESSION  (COURSE 1 --< 0..* "has", USER 1 --< 0..* "creates")
-- SharedVersion sessions use the pinned rubric version; Legacy sessions retain question rubrics.
CREATE TABLE exam_sessions (
    id                             UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    course_id                      UUID         NOT NULL REFERENCES courses(id),
    rubric_mode                    VARCHAR(20)  NOT NULL DEFAULT 'SharedVersion',
    rubric_version_id              UUID,
    created_by                     UUID         NOT NULL REFERENCES users(id),
    title                          VARCHAR(200) NOT NULL,
    start_time                     TIMESTAMPTZ  NOT NULL,
    end_time                       TIMESTAMPTZ  NOT NULL,
    time_limit_per_answer_seconds  INT          NOT NULL CHECK (time_limit_per_answer_seconds > 0),
    max_follow_ups                 INT          NOT NULL DEFAULT 2 CHECK (max_follow_ups >= 0),
    status                         VARCHAR(20)  NOT NULL DEFAULT 'Draft'
                                   CHECK (status IN ('Draft', 'Published', 'Closed')),
    created_at                     TIMESTAMPTZ  NOT NULL DEFAULT now(),   -- technical column
    CONSTRAINT ck_exam_sessions_rubric_mode
        CHECK (rubric_mode IN ('Legacy', 'SharedVersion')),
    CONSTRAINT fk_exam_sessions_rubric_version
        FOREIGN KEY (rubric_version_id) REFERENCES rubric_versions(id) ON DELETE RESTRICT,
    CONSTRAINT ck_exam_sessions_rubric_configuration CHECK (
        (rubric_mode = 'Legacy' AND rubric_version_id IS NULL)
        OR (rubric_mode = 'SharedVersion' AND (rubric_version_id IS NOT NULL OR status = 'Draft'))
    ),
    CHECK (end_time > start_time)
);
CREATE INDEX ix_exam_sessions_course ON exam_sessions(course_id);
CREATE INDEX ix_exam_sessions_rubric_version ON exam_sessions(rubric_version_id)
    WHERE rubric_version_id IS NOT NULL;

-- ERD: QUESTION  (EXAM SESSION 1 --< 0..* "includes"; a draft may have no questions yet)
CREATE TABLE questions (
    id               UUID  PRIMARY KEY DEFAULT gen_random_uuid(),
    exam_session_id  UUID  NOT NULL REFERENCES exam_sessions(id) ON DELETE CASCADE,
    order_no         INT   NOT NULL CHECK (order_no > 0),
    content          TEXT  NOT NULL,
    source_bank_revision_id UUID REFERENCES question_bank_revisions(id),
    UNIQUE (exam_session_id, order_no)
);

-- ERD: RUBRIC  (QUESTION 1 -- 0..1 "is graded by")
-- max_score = sum of rubric_criteria.max_points (kept by trigger trg_rubric_max_score)
CREATE TABLE rubrics (
    id           UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    question_id  UUID          NOT NULL UNIQUE REFERENCES questions(id) ON DELETE CASCADE,
    max_score    NUMERIC(5,2)  NOT NULL DEFAULT 0 CHECK (max_score >= 0)
);

-- ERD: RUBRIC CRITERION  (RUBRIC 1 --< 1..* "has")
CREATE TABLE rubric_criteria (
    id                UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    rubric_id         UUID          REFERENCES rubrics(id) ON DELETE CASCADE,
    rubric_version_id UUID,
    order_no          INT           NOT NULL CHECK (order_no > 0),
    description       TEXT          NOT NULL,
    max_points        NUMERIC(5,2)  NOT NULL CHECK (max_points > 0),
    CONSTRAINT fk_rubric_criteria_version
        FOREIGN KEY (rubric_version_id) REFERENCES rubric_versions(id) ON DELETE RESTRICT,
    CONSTRAINT ck_rubric_criteria_exactly_one_parent
        CHECK ((rubric_id IS NOT NULL) <> (rubric_version_id IS NOT NULL)),
    UNIQUE (rubric_id, order_no)
);
CREATE UNIQUE INDEX ux_rubric_criteria_version_order
    ON rubric_criteria(rubric_version_id, order_no)
    WHERE rubric_version_id IS NOT NULL;

CREATE OR REPLACE FUNCTION assert_rubric_has_criteria(p_rubric_id UUID) RETURNS VOID AS $$
DECLARE
    criterion_count BIGINT;
    calculated_max_score NUMERIC;
    stored_max_score NUMERIC(5,2);
BEGIN
    SELECT max_score INTO stored_max_score FROM rubrics WHERE id = p_rubric_id;
    IF NOT FOUND THEN
        RETURN;
    END IF;

    SELECT COUNT(*), COALESCE(SUM(max_points), 0)
    INTO criterion_count, calculated_max_score
    FROM rubric_criteria
    WHERE rubric_id = p_rubric_id;

    IF criterion_count = 0 THEN
        RAISE EXCEPTION 'Rubric % must have at least one criterion.', p_rubric_id
            USING ERRCODE = '23514';
    END IF;
    IF calculated_max_score <= 0 THEN
        RAISE EXCEPTION 'Rubric % must have a positive total max score.', p_rubric_id
            USING ERRCODE = '23514';
    END IF;
    IF stored_max_score IS DISTINCT FROM calculated_max_score THEN
        RAISE EXCEPTION 'Rubric % max_score does not match its criteria.', p_rubric_id
            USING ERRCODE = '23514';
    END IF;
END;
$$ LANGUAGE plpgsql;

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
$$ LANGUAGE plpgsql;

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
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER trg_rubrics_require_criteria
AFTER INSERT OR UPDATE OR DELETE ON rubrics
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION check_rubric_criteria_constraints();

CREATE CONSTRAINT TRIGGER trg_rubric_versions_require_criteria
AFTER INSERT OR UPDATE OR DELETE ON rubric_versions
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION check_rubric_criteria_constraints();

CREATE CONSTRAINT TRIGGER trg_rubric_criteria_require_parent
AFTER INSERT OR UPDATE OR DELETE ON rubric_criteria
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION check_rubric_criteria_constraints();

-- ---------------------------------------------------------------------
-- F3 - AI Viva Interview
-- ---------------------------------------------------------------------

-- ERD: PARTICIPANT  (EXAM SESSION 1 --< 0..* "has", USER 1 --< 0..* "joins as")
-- A student on the session list, imported by the lecturer.
-- joined_at is required by the class diagram: the time the student was added to the list.
CREATE TABLE participants (
    id               UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    exam_session_id  UUID        NOT NULL REFERENCES exam_sessions(id) ON DELETE CASCADE,
    student_id       UUID        NOT NULL REFERENCES users(id),
    joined_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    status           VARCHAR(20) NOT NULL DEFAULT 'Registered'
                     CHECK (status IN ('Registered', 'InProgress', 'Completed')),
    UNIQUE (exam_session_id, student_id)
);
CREATE INDEX ix_participants_student ON participants(student_id);

-- ERD: INTERVIEW ATTEMPT  (PARTICIPANT 1 --< 0..* "makes")
-- A participant may have multiple attempts.
CREATE TABLE interview_attempts (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    participant_id  UUID        NOT NULL REFERENCES participants(id) ON DELETE CASCADE,
    started_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at    TIMESTAMPTZ,
    status          VARCHAR(20) NOT NULL DEFAULT 'InProgress'
                    CHECK (status IN ('InProgress', 'Interrupted', 'PendingReview', 'Finalized')),
    CHECK (completed_at IS NULL OR completed_at >= started_at)
);
CREATE INDEX ix_interview_attempts_participant ON interview_attempts(participant_id);

-- ERD: QUESTION RESPONSE  (ATTEMPT 1 --< 0..* "contains", QUESTION 1 --< 0..* "is answered in")
-- A question not reached before the session closes has no row here and counts as 0.
-- ai_suggested_score = sum of criterion_scores.points (kept by trigger trg_response_ai_score).
CREATE TABLE question_responses (
    id                  UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    attempt_id          UUID          NOT NULL REFERENCES interview_attempts(id) ON DELETE CASCADE,
    question_id         UUID          NOT NULL REFERENCES questions(id),
    follow_up_count     INT           NOT NULL DEFAULT 0 CHECK (follow_up_count >= 0),
    is_finished         BOOLEAN       NOT NULL DEFAULT FALSE,
    ai_suggested_score  NUMERIC(5,2)  CHECK (ai_suggested_score >= 0),
    ai_feedback         TEXT,
    evaluated_at        TIMESTAMPTZ,
    ai_evaluation_state VARCHAR(20) NOT NULL DEFAULT 'NotStarted'
                        CHECK (ai_evaluation_state IN ('NotStarted', 'Pending', 'Failed', 'Completed')),
    ai_evaluation_error VARCHAR(500),
    UNIQUE (attempt_id, question_id)
);
CREATE INDEX ix_responses_question ON question_responses(question_id);

-- ERD: INTERVIEW TURN  (QUESTION RESPONSE 1 --< 1..* "consists of")
CREATE TABLE interview_turns (
    id                    UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    question_response_id  UUID         NOT NULL REFERENCES question_responses(id) ON DELETE CASCADE,
    turn_no               INT          NOT NULL CHECK (turn_no > 0),
    type                  VARCHAR(10)  NOT NULL CHECK (type IN ('Main', 'FollowUp')),
    question_text         TEXT         NOT NULL,   -- exact text of the question asked (follow-ups too)
    answer_transcript     TEXT,                    -- NULL when time ran out without an answer
    audio_url             VARCHAR(500),            -- the audio file lives in object storage
    asked_at              TIMESTAMPTZ  NOT NULL DEFAULT now(),
    answered_at           TIMESTAMPTZ,
    is_timed_out          BOOLEAN      NOT NULL DEFAULT FALSE,
    processing_status     VARCHAR(20)  NOT NULL DEFAULT 'AwaitingAnswer',
    processing_started_at TIMESTAMPTZ,
    retry_count           INT          NOT NULL DEFAULT 0 CHECK (retry_count >= 0),
    next_retry_at         TIMESTAMPTZ,
    processing_token      UUID,
    submission_result     JSONB,
    CONSTRAINT ck_interview_turns_processing_status
        CHECK (processing_status IN ('AwaitingAnswer', 'Processing', 'Retry', 'Completed', 'Failed')),
    CONSTRAINT ck_interview_turns_processing_token
        CHECK ((processing_status = 'Processing') = (processing_token IS NOT NULL)),
    UNIQUE (question_response_id, turn_no)
);
CREATE INDEX ix_interview_turns_processing_recovery
    ON interview_turns(processing_status, processing_started_at)
    WHERE processing_status IN ('Processing', 'Retry');
CREATE INDEX ix_interview_turns_retry_due
    ON interview_turns(processing_status, next_retry_at)
    WHERE processing_status = 'Retry';

-- ERD: CRITERION SCORE  (QUESTION RESPONSE 1 --< 0..* "is scored by", RUBRIC CRITERION 1 --< 0..* "is scored in")
-- Trigger trg_criterion_score_check: the criterion must belong to the rubric of the response's
-- question, and points <= max_points.
CREATE TABLE criterion_scores (
    id                    UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    question_response_id  UUID          NOT NULL REFERENCES question_responses(id) ON DELETE CASCADE,
    rubric_criterion_id   UUID          NOT NULL REFERENCES rubric_criteria(id),
    points                NUMERIC(5,2)  NOT NULL CHECK (points >= 0),
    comment               TEXT,
    UNIQUE (question_response_id, rubric_criterion_id)
);
CREATE INDEX ix_criterion_scores_criterion ON criterion_scores(rubric_criterion_id);

-- ERD: SCORE REVIEW  (ATTEMPT 1 -- 0..1 "is finalized by", USER 1 --< 0..* "reviews")
-- Starts as Pending at the sum of the AI-suggested scores; counts as confirmed only when Approved.
-- reviewer_id is required by the diagram, so a Pending review is assigned to a lecturer up front.
CREATE TABLE score_reviews (
    id           UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    attempt_id   UUID          NOT NULL UNIQUE REFERENCES interview_attempts(id) ON DELETE CASCADE,
    reviewer_id  UUID          NOT NULL REFERENCES users(id),
    final_score  NUMERIC(5,2)  NOT NULL CHECK (final_score >= 0),
    comment      TEXT,
    status       VARCHAR(20)   NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Approved')),
    reviewed_at  TIMESTAMPTZ,
    CHECK ((status = 'Approved') = (reviewed_at IS NOT NULL))
);
CREATE INDEX ix_score_reviews_reviewer ON score_reviews(reviewer_id);

-- ---------------------------------------------------------------------
-- Triggers for derived values and cross-table rules
-- ---------------------------------------------------------------------

-- rubrics.max_score = SUM(rubric_criteria.max_points)
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
END $$ LANGUAGE plpgsql;

CREATE TRIGGER trg_rubric_max_score
AFTER INSERT OR UPDATE OR DELETE ON rubric_criteria
FOR EACH ROW EXECUTE FUNCTION fn_sync_rubric_max_score();

-- criterion_scores: the criterion belongs to the right rubric, and points <= max_points
CREATE OR REPLACE FUNCTION fn_check_criterion_score() RETURNS trigger AS $$
DECLARE
    v_max NUMERIC(5,2);
BEGIN
    SELECT c.max_points INTO v_max
      FROM rubric_criteria c
      JOIN rubrics r             ON r.id = c.rubric_id
      JOIN question_responses qr ON qr.question_id = r.question_id
      JOIN interview_attempts ia ON ia.id = qr.attempt_id
      JOIN participants p        ON p.id = ia.participant_id
      JOIN exam_sessions s       ON s.id = p.exam_session_id
     WHERE c.id = NEW.rubric_criterion_id
       AND qr.id = NEW.question_response_id
       AND c.rubric_version_id IS NULL
       AND s.rubric_mode = 'Legacy';

    IF NOT FOUND THEN
        SELECT c.max_points INTO v_max
          FROM rubric_criteria c
          JOIN question_responses qr ON qr.id = NEW.question_response_id
          JOIN interview_attempts ia ON ia.id = qr.attempt_id
          JOIN participants p        ON p.id = ia.participant_id
          JOIN exam_sessions s       ON s.id = p.exam_session_id
          JOIN questions q           ON q.id = qr.question_id AND q.exam_session_id = s.id
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
END $$ LANGUAGE plpgsql;

CREATE TRIGGER trg_criterion_score_check
BEFORE INSERT OR UPDATE ON criterion_scores
FOR EACH ROW EXECUTE FUNCTION fn_check_criterion_score();

CREATE OR REPLACE FUNCTION fn_lock_rubric_activation() RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(5067184210567320137);
    RETURN NULL;
END;
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;

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

CREATE TRIGGER trg_rubric_versions_immutable
BEFORE INSERT OR UPDATE OR DELETE ON rubric_versions
FOR EACH ROW EXECUTE FUNCTION fn_protect_rubric_version();

CREATE OR REPLACE FUNCTION fn_reject_rubric_truncate() RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'Rubric versions and criteria cannot be truncated.'
        USING ERRCODE = '23514';
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

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
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_rubric_criteria_immutable
BEFORE INSERT OR UPDATE OR DELETE ON rubric_criteria
FOR EACH ROW EXECUTE FUNCTION fn_protect_sealed_rubric_criteria();

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
       AND (NEW.rubric_version_id IS DISTINCT FROM OLD.rubric_version_id OR NEW.status = 'Draft') THEN
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
$$ LANGUAGE plpgsql;

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
$$ LANGUAGE plpgsql;

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
    FROM exam_sessions
    WHERE id = v_session_id;
    IF NOT FOUND OR v_mode <> 'SharedVersion' OR v_status = 'Draft' THEN
        RETURN NULL;
    END IF;

    SELECT max_score INTO v_version_max
    FROM rubric_versions
    WHERE id = v_version_id AND is_sealed;
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
$$ LANGUAGE plpgsql;

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
$$ LANGUAGE plpgsql;

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
$$ LANGUAGE plpgsql;

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
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_participants_published_shared_protection
BEFORE UPDATE OR DELETE ON participants
FOR EACH ROW EXECUTE FUNCTION fn_protect_published_shared_participants();

-- question_responses.ai_suggested_score = SUM(criterion_scores.points)
CREATE OR REPLACE FUNCTION fn_sync_response_ai_score() RETURNS trigger AS $$
BEGIN
    UPDATE question_responses q
       SET ai_suggested_score = (SELECT SUM(s.points) FROM criterion_scores s WHERE s.question_response_id = q.id)
     WHERE q.id IN (COALESCE(NEW.question_response_id, OLD.question_response_id),
                    COALESCE(OLD.question_response_id, NEW.question_response_id));
    RETURN NULL;
END $$ LANGUAGE plpgsql;

CREATE TRIGGER trg_response_ai_score
AFTER INSERT OR UPDATE OR DELETE ON criterion_scores
FOR EACH ROW EXECUTE FUNCTION fn_sync_response_ai_score();
