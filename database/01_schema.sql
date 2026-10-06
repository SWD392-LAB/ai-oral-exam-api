-- =====================================================================
-- AIVES - AI-powered Viva Exam System
-- 01_schema.sql : creates the tables for PostgreSQL (>= 13)
-- Based on the Conceptual ERD (Figure 8) + attributes from the Class Diagram (Figure 9)
--   15 ERD entities + 1 join table course_lecturers (N-N "teaches" relationship)
-- Enums are stored as VARCHAR + CHECK so they map easily to EF Core (string conversion).
-- =====================================================================

-- Re-runnable: drop old tables first (reverse dependency order)
DROP TABLE IF EXISTS score_reviews, ai_evaluations, interview_turns, question_responses,
    interview_attempts, participants, rubrics, questions, exam_sessions,
    course_lecturers, courses, ai_service_configs, audit_logs,
    password_setup_tokens, users, roles CASCADE;

-- ---------------------------------------------------------------------
-- F7 - Access Control
-- ---------------------------------------------------------------------

-- ERD: ROLE  (Class diagram: enum UserRole)
CREATE TABLE roles (
    id          SMALLINT     PRIMARY KEY,
    name        VARCHAR(20)  NOT NULL UNIQUE
                CHECK (name IN ('Student', 'Lecturer', 'Administrator'))
);

-- ERD: USER  (Class diagram: User + Student/Lecturer/Administrator -> one table)
CREATE TABLE users (
    id              UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    full_name       VARCHAR(150) NOT NULL,
    email           VARCHAR(255) NOT NULL UNIQUE,
    role_id         SMALLINT     NOT NULL REFERENCES roles(id),
    auth_provider   VARCHAR(20)  NOT NULL DEFAULT 'Password'
                    CHECK (auth_provider IN ('Password', 'GoogleSSO')),
    password_hash   VARCHAR(512),          -- NULL when no password is set yet / Google SSO
    password_salt   VARCHAR(128),          -- kept from the class diagram; NULL when using Identity PasswordHasher (salt is inside the hash)
    student_code    VARCHAR(20)  UNIQUE,   -- Student only
    lecturer_code   VARCHAR(20)  UNIQUE,   -- Lecturer only
    is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT now()
);
CREATE INDEX ix_users_role ON users(role_id);

-- ERD: PASSWORD SETUP TOKEN  (USER 1 --< 0..*)
CREATE TABLE password_setup_tokens (
    id          UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     UUID         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash  VARCHAR(128) NOT NULL UNIQUE,   -- only the token hash is stored, never the raw token
    purpose     VARCHAR(20)  NOT NULL CHECK (purpose IN ('AccountSetup', 'PasswordReset')),
    expires_at  TIMESTAMPTZ  NOT NULL,
    used_at     TIMESTAMPTZ,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
);
CREATE INDEX ix_pwd_tokens_user ON password_setup_tokens(user_id);

-- ERD: AUDIT LOG  (USER 1 --< 0..* "performs")
CREATE TABLE audit_logs (
    id             UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id        UUID         NOT NULL REFERENCES users(id),
    action         VARCHAR(100) NOT NULL,
    target_entity  VARCHAR(100) NOT NULL,
    target_id      UUID,
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
    updated_at         TIMESTAMPTZ  NOT NULL DEFAULT now()
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
CREATE TABLE course_lecturers (
    course_id    UUID        NOT NULL REFERENCES courses(id) ON DELETE CASCADE,
    lecturer_id  UUID        NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    assigned_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (course_id, lecturer_id)
);
CREATE INDEX ix_course_lecturers_lecturer ON course_lecturers(lecturer_id);

-- ERD: EXAM SESSION  (COURSE 1 --< 0..*, USER 1 --< 0..* "creates")
CREATE TABLE exam_sessions (
    id                       UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    course_id                UUID         NOT NULL REFERENCES courses(id),
    created_by               UUID         NOT NULL REFERENCES users(id),
    title                    VARCHAR(200) NOT NULL,
    start_time               TIMESTAMPTZ  NOT NULL,
    end_time                 TIMESTAMPTZ  NOT NULL,
    time_limit_per_question  INT          NOT NULL CHECK (time_limit_per_question > 0),  -- seconds
    max_follow_ups           INT          NOT NULL DEFAULT 2 CHECK (max_follow_ups >= 0),
    status                   VARCHAR(20)  NOT NULL DEFAULT 'Draft'
                             CHECK (status IN ('Draft', 'Published', 'Closed')),
    created_at               TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CHECK (end_time > start_time)
);
CREATE INDEX ix_exam_sessions_course ON exam_sessions(course_id);

-- ERD: QUESTION  (EXAM SESSION 1 --< 1..*)
CREATE TABLE questions (
    id               UUID  PRIMARY KEY DEFAULT gen_random_uuid(),
    exam_session_id  UUID  NOT NULL REFERENCES exam_sessions(id) ON DELETE CASCADE,
    order_no         INT   NOT NULL CHECK (order_no > 0),
    content          TEXT  NOT NULL,
    UNIQUE (exam_session_id, order_no)
);

-- ERD: RUBRIC  (QUESTION 1 -- 1)
CREATE TABLE rubrics (
    id           UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    question_id  UUID          NOT NULL UNIQUE REFERENCES questions(id) ON DELETE CASCADE,
    criteria     TEXT          NOT NULL,
    max_score    NUMERIC(5,2)  NOT NULL CHECK (max_score > 0)
);

-- ---------------------------------------------------------------------
-- F3 - AI Viva Interview
-- ---------------------------------------------------------------------

-- ERD: PARTICIPANT  (EXAM SESSION 1 --< 0..*, USER 1 --< 0..* "joins as")
CREATE TABLE participants (
    id               UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    exam_session_id  UUID        NOT NULL REFERENCES exam_sessions(id) ON DELETE CASCADE,
    student_id       UUID        NOT NULL REFERENCES users(id),
    joined_at        TIMESTAMPTZ,
    status           VARCHAR(20) NOT NULL DEFAULT 'Registered'
                     CHECK (status IN ('Registered', 'InProgress', 'Completed')),
    UNIQUE (exam_session_id, student_id)
);
CREATE INDEX ix_participants_student ON participants(student_id);

-- ERD: INTERVIEW ATTEMPT  (PARTICIPANT 1 --< 0..* "makes")
CREATE TABLE interview_attempts (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    participant_id  UUID        NOT NULL REFERENCES participants(id) ON DELETE CASCADE,
    started_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at    TIMESTAMPTZ,
    status          VARCHAR(20) NOT NULL DEFAULT 'InProgress'
                    CHECK (status IN ('InProgress', 'PendingReview', 'Finalized'))
);
CREATE INDEX ix_attempts_participant ON interview_attempts(participant_id);

-- ERD: QUESTION RESPONSE  (ATTEMPT 1 --< 1..* "contains", QUESTION 1 --< 0..* "is answered in")
CREATE TABLE question_responses (
    id               UUID     PRIMARY KEY DEFAULT gen_random_uuid(),
    attempt_id       UUID     NOT NULL REFERENCES interview_attempts(id) ON DELETE CASCADE,
    question_id      UUID     NOT NULL REFERENCES questions(id),
    follow_up_count  INT      NOT NULL DEFAULT 0 CHECK (follow_up_count >= 0),
    is_finished      BOOLEAN  NOT NULL DEFAULT FALSE,
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

-- ERD: AI EVALUATION  (QUESTION RESPONSE 1 -- 0..1 "receives")
CREATE TABLE ai_evaluations (
    id                    UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    question_response_id  UUID          NOT NULL UNIQUE REFERENCES question_responses(id) ON DELETE CASCADE,
    suggested_score       NUMERIC(5,2)  NOT NULL CHECK (suggested_score >= 0),
    feedback              TEXT          NOT NULL,
    evaluated_at          TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- ERD: SCORE REVIEW  (ATTEMPT 1 -- 0..1 "is finalized by", USER 1 --< 0..* "reviews")
CREATE TABLE score_reviews (
    id           UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    attempt_id   UUID          NOT NULL UNIQUE REFERENCES interview_attempts(id) ON DELETE CASCADE,
    reviewer_id  UUID          NOT NULL REFERENCES users(id),
    final_score  NUMERIC(5,2)  NOT NULL CHECK (final_score >= 0),
    comment      TEXT,
    status       VARCHAR(20)   NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Approved')),
    reviewed_at  TIMESTAMPTZ
);
CREATE INDEX ix_score_reviews_reviewer ON score_reviews(reviewer_id);
