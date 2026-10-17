-- =====================================================================
-- AIVES - AI-powered Viva Exam System
-- 01_schema.sql : creates the tables for PostgreSQL (>= 13)
-- Follows docs/ConceptualERD.drawio + docs/ClassDiagram.drawio (15 entities)
--   + 1 join table course_lecturers (N-N "teaches" relationship).
-- Enums are stored as VARCHAR + CHECK so they map easily to EF Core (string conversion).
-- Derived values (rubrics.max_score, question_responses.ai_suggested_score) are kept
-- in sync by triggers at the end of this file.
-- =====================================================================

-- Re-runnable: drop old tables first (old + new names, reverse dependency order)
DROP TABLE IF EXISTS criterion_scores, score_reviews, ai_evaluations, interview_turns,
    question_responses, interview_attempts, participants, rubric_criteria, rubrics,
    questions, exam_sessions, course_lecturers, courses, ai_service_configs, audit_logs,
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

-- ERD: EXAM SESSION  (COURSE 1 --< 0..* "has", USER 1 --< 0..* "creates")
-- Publish rule (application): >= 1 question, every question has a rubric, >= 1 participant.
CREATE TABLE exam_sessions (
    id                             UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    course_id                      UUID         NOT NULL REFERENCES courses(id),
    created_by                     UUID         NOT NULL REFERENCES users(id),
    title                          VARCHAR(200) NOT NULL,
    start_time                     TIMESTAMPTZ  NOT NULL,
    end_time                       TIMESTAMPTZ  NOT NULL,
    time_limit_per_answer_seconds  INT          NOT NULL CHECK (time_limit_per_answer_seconds > 0),
    max_follow_ups                 INT          NOT NULL DEFAULT 2 CHECK (max_follow_ups >= 0),
    status                         VARCHAR(20)  NOT NULL DEFAULT 'Draft'
                                   CHECK (status IN ('Draft', 'Published', 'Closed')),
    created_at                     TIMESTAMPTZ  NOT NULL DEFAULT now(),   -- technical column
    CHECK (end_time > start_time)
);
CREATE INDEX ix_exam_sessions_course ON exam_sessions(course_id);

-- ERD: QUESTION  (EXAM SESSION 1 --< 0..* "includes"; a draft may have no questions yet)
CREATE TABLE questions (
    id               UUID  PRIMARY KEY DEFAULT gen_random_uuid(),
    exam_session_id  UUID  NOT NULL REFERENCES exam_sessions(id) ON DELETE CASCADE,
    order_no         INT   NOT NULL CHECK (order_no > 0),
    content          TEXT  NOT NULL,
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
    id           UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    rubric_id    UUID          NOT NULL REFERENCES rubrics(id) ON DELETE CASCADE,
    order_no     INT           NOT NULL CHECK (order_no > 0),
    description  TEXT          NOT NULL,
    max_points   NUMERIC(5,2)  NOT NULL CHECK (max_points > 0),
    UNIQUE (rubric_id, order_no)
);

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

CREATE OR REPLACE FUNCTION check_rubric_criteria_constraints() RETURNS TRIGGER AS $$
BEGIN
    IF TG_TABLE_NAME = 'rubrics' THEN
        IF TG_OP <> 'DELETE' THEN
            PERFORM assert_rubric_has_criteria(NEW.id);
        END IF;
    ELSE
        IF TG_OP <> 'INSERT' THEN
            PERFORM assert_rubric_has_criteria(OLD.rubric_id);
        END IF;
        IF TG_OP <> 'DELETE' THEN
            PERFORM assert_rubric_has_criteria(NEW.rubric_id);
        END IF;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER trg_rubrics_require_criteria
AFTER INSERT OR UPDATE OR DELETE ON rubrics
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
-- Business rule (diagram note): one attempt per participant -> UNIQUE (participant_id).
CREATE TABLE interview_attempts (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    participant_id  UUID        NOT NULL UNIQUE REFERENCES participants(id) ON DELETE CASCADE,
    started_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at    TIMESTAMPTZ,
    status          VARCHAR(20) NOT NULL DEFAULT 'InProgress'
                    CHECK (status IN ('InProgress', 'PendingReview', 'Finalized')),
    CHECK (completed_at IS NULL OR completed_at >= started_at)
);

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
    UNIQUE (question_response_id, turn_no)
);

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
    UPDATE rubrics r
       SET max_score = COALESCE((SELECT SUM(c.max_points) FROM rubric_criteria c WHERE c.rubric_id = r.id), 0)
     WHERE r.id IN (COALESCE(NEW.rubric_id, OLD.rubric_id), COALESCE(OLD.rubric_id, NEW.rubric_id));
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
      JOIN rubrics r            ON r.id = c.rubric_id
      JOIN question_responses q ON q.question_id = r.question_id
     WHERE c.id = NEW.rubric_criterion_id AND q.id = NEW.question_response_id;
    IF v_max IS NULL THEN
        RAISE EXCEPTION 'Criterion % does not belong to the rubric of the question answered in response %',
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
