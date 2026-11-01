\set ON_ERROR_STOP on

BEGIN;
SET LOCAL search_path TO fresh;

DO $$
BEGIN
    IF current_database() NOT IN ('aives_f1_fresh_test', 'aives_f1_migration_test')
       OR current_schema() IS DISTINCT FROM 'fresh' THEN
        RAISE EXCEPTION 'Refusing to run: connect to aives_f1_fresh_test or aives_f1_migration_test with schema fresh active.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM fresh.roles WHERE name = 'Lecturer')
       OR NOT EXISTS (SELECT 1 FROM fresh.roles WHERE name = 'Student') THEN
        RAISE EXCEPTION 'Required Lecturer and Student roles were not found.';
    END IF;
END;
$$;

CREATE TEMP TABLE f1a1_test_ids ON COMMIT DROP AS
SELECT gen_random_uuid() AS lecturer_id,
       gen_random_uuid() AS student_id,
       gen_random_uuid() AS course_id,
       gen_random_uuid() AS template_id,
       gen_random_uuid() AS version_id,
       gen_random_uuid() AS shared_session_id,
       gen_random_uuid() AS invalid_publish_session_id,
       gen_random_uuid() AS shared_question_id,
       gen_random_uuid() AS shared_participant_id,
       gen_random_uuid() AS shared_attempt_id,
       gen_random_uuid() AS shared_response_id,
       gen_random_uuid() AS legacy_session_id,
       gen_random_uuid() AS legacy_question_id,
       gen_random_uuid() AS legacy_rubric_id,
       gen_random_uuid() AS legacy_participant_id,
       gen_random_uuid() AS legacy_attempt_id,
       gen_random_uuid() AS legacy_response_id;

INSERT INTO fresh.users (id, role_id, full_name, email, username, lecturer_code, is_active)
SELECT x.lecturer_id, r.id, 'F1A1 PostgreSQL Lecturer',
       x.lecturer_id::text || '@f1a1.test', 'f1a1_' || replace(x.lecturer_id::text, '-', ''),
       left(replace(x.lecturer_id::text, '-', ''), 20), TRUE
FROM f1a1_test_ids x
JOIN fresh.roles r ON r.name = 'Lecturer';

INSERT INTO fresh.users (id, role_id, full_name, email, username, student_code, is_active)
SELECT x.student_id, r.id, 'F1A1 PostgreSQL Student',
       x.student_id::text || '@f1a1.test', 'f1a1_' || replace(x.student_id::text, '-', ''),
       left(replace(x.student_id::text, '-', ''), 20), TRUE
FROM f1a1_test_ids x
JOIN fresh.roles r ON r.name = 'Student';

INSERT INTO fresh.courses (id, code, name)
SELECT course_id, 'F1A1-' || left(replace(course_id::text, '-', ''), 15), 'F1A1 PostgreSQL verification'
FROM f1a1_test_ids;

INSERT INTO fresh.course_lecturers (course_id, lecturer_id)
SELECT course_id, lecturer_id FROM f1a1_test_ids;

INSERT INTO fresh.rubric_templates (id, course_id, name, created_by)
SELECT template_id, course_id, 'F1A1 PostgreSQL verification', lecturer_id
FROM f1a1_test_ids;

INSERT INTO fresh.rubric_versions (id, template_id, course_id, version_no, created_by)
SELECT version_id, template_id, course_id, 1, lecturer_id
FROM f1a1_test_ids;

INSERT INTO fresh.rubric_criteria (id, rubric_version_id, order_no, description, max_points)
SELECT gen_random_uuid(), x.version_id, c.order_no, c.description, c.max_points
FROM f1a1_test_ids x
CROSS JOIN (VALUES (1, 'Accuracy', 5.00::numeric), (2, 'Clarity', 5.00::numeric))
    AS c(order_no, description, max_points);

UPDATE fresh.rubric_versions v
SET is_sealed = TRUE
FROM f1a1_test_ids x
WHERE v.id = x.version_id;

SET CONSTRAINTS ALL IMMEDIATE;

DO $$
DECLARE
    v_version_id uuid;
    v_max_score numeric(5,2);
    v_is_sealed boolean;
BEGIN
    SELECT version_id INTO v_version_id FROM f1a1_test_ids;
    SELECT max_score, is_sealed INTO v_max_score, v_is_sealed
    FROM fresh.rubric_versions WHERE id = v_version_id;
    IF v_max_score IS DISTINCT FROM 10.00::numeric OR NOT v_is_sealed THEN
        RAISE EXCEPTION 'Version sealing or trigger-generated max_score failed: %, %', v_is_sealed, v_max_score;
    END IF;
END;
$$;

DO $$
DECLARE
    v_version_id uuid;
    v_criterion_id uuid;
    v_rejected boolean;
    v_message text;
BEGIN
    SELECT version_id INTO v_version_id FROM f1a1_test_ids;
    SELECT id INTO v_criterion_id FROM fresh.rubric_criteria
    WHERE rubric_version_id = v_version_id AND order_no = 1;

    v_rejected := FALSE;
    BEGIN
        UPDATE fresh.rubric_criteria SET description = 'Mutated' WHERE id = v_criterion_id;
    EXCEPTION WHEN SQLSTATE '23514' THEN
        GET STACKED DIAGNOSTICS v_message = MESSAGE_TEXT;
        IF v_message <> 'Criteria belonging to a rubric version are append-only.' THEN
            RAISE EXCEPTION 'Unexpected criterion update rejection: %', v_message;
        END IF;
        v_rejected := TRUE;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'Sealed criterion update was not rejected.'; END IF;

    v_rejected := FALSE;
    BEGIN
        DELETE FROM fresh.rubric_criteria WHERE id = v_criterion_id;
    EXCEPTION WHEN SQLSTATE '23514' THEN
        GET STACKED DIAGNOSTICS v_message = MESSAGE_TEXT;
        IF v_message <> 'Criteria belonging to a rubric version are append-only.' THEN
            RAISE EXCEPTION 'Unexpected criterion deletion rejection: %', v_message;
        END IF;
        v_rejected := TRUE;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'Sealed criterion deletion was not rejected.'; END IF;

    v_rejected := FALSE;
    BEGIN
        INSERT INTO fresh.rubric_criteria (id, rubric_version_id, order_no, description, max_points)
        VALUES (gen_random_uuid(), v_version_id, 3, 'Late criterion', 1.00);
    EXCEPTION WHEN SQLSTATE '23514' THEN
        GET STACKED DIAGNOSTICS v_message = MESSAGE_TEXT;
        IF v_message <> 'Criteria can only be inserted into an unsealed rubric version.' THEN
            RAISE EXCEPTION 'Unexpected sealed criterion insert rejection: %', v_message;
        END IF;
        v_rejected := TRUE;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'Criterion insert into a sealed version was not rejected.'; END IF;

    v_rejected := FALSE;
    BEGIN
        UPDATE fresh.rubric_versions SET version_no = version_no + 1 WHERE id = v_version_id;
    EXCEPTION WHEN SQLSTATE '23514' THEN
        GET STACKED DIAGNOSTICS v_message = MESSAGE_TEXT;
        IF v_message <> 'Rubric versions are immutable; only sealing an unsealed version is allowed.' THEN
            RAISE EXCEPTION 'Unexpected sealed version update rejection: %', v_message;
        END IF;
        v_rejected := TRUE;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'Sealed version update was not rejected.'; END IF;

    v_rejected := FALSE;
    BEGIN
        DELETE FROM fresh.rubric_versions WHERE id = v_version_id;
    EXCEPTION WHEN SQLSTATE '23514' THEN
        GET STACKED DIAGNOSTICS v_message = MESSAGE_TEXT;
        IF v_message <> 'Rubric versions are append-only and cannot be deleted.' THEN
            RAISE EXCEPTION 'Unexpected sealed version deletion rejection: %', v_message;
        END IF;
        v_rejected := TRUE;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'Sealed version deletion was not rejected.'; END IF;
END;
$$;

INSERT INTO fresh.exam_sessions
    (id, course_id, rubric_mode, rubric_version_id, created_by, title,
     start_time, end_time, time_limit_per_answer_seconds, status)
SELECT shared_session_id, course_id, 'SharedVersion', version_id, lecturer_id,
       'F1A1 shared verification', now() - interval '1 minute', now() + interval '1 hour', 60, 'Draft'
FROM f1a1_test_ids;

INSERT INTO fresh.questions (id, exam_session_id, order_no, content)
SELECT shared_question_id, shared_session_id, 1, 'F1A1 shared question'
FROM f1a1_test_ids;

INSERT INTO fresh.participants (id, exam_session_id, student_id)
SELECT shared_participant_id, shared_session_id, student_id
FROM f1a1_test_ids;

INSERT INTO fresh.interview_attempts (id, participant_id)
SELECT shared_attempt_id, shared_participant_id FROM f1a1_test_ids;

INSERT INTO fresh.question_responses (id, attempt_id, question_id)
SELECT shared_response_id, shared_attempt_id, shared_question_id FROM f1a1_test_ids;

INSERT INTO fresh.criterion_scores (id, question_response_id, rubric_criterion_id, points)
SELECT gen_random_uuid(), x.shared_response_id, c.id,
       CASE c.order_no WHEN 1 THEN 4.50 ELSE 3.25 END
FROM f1a1_test_ids x
JOIN fresh.rubric_criteria c ON c.rubric_version_id = x.version_id;

DO $$
DECLARE
    v_response_id uuid;
    v_score numeric(5,2);
    v_rejected boolean;
    v_criterion_id uuid;
    v_message text;
BEGIN
    SELECT shared_response_id INTO v_response_id FROM f1a1_test_ids;
    SELECT ai_suggested_score INTO v_score
    FROM fresh.question_responses WHERE id = v_response_id;
    IF v_score IS DISTINCT FROM 7.75::numeric THEN
        RAISE EXCEPTION 'Response score trigger mismatch: got %; expected 7.75.', v_score;
    END IF;

    SELECT c.id INTO v_criterion_id
    FROM fresh.rubric_criteria c
    JOIN f1a1_test_ids x ON c.rubric_version_id = x.version_id
    WHERE c.order_no = 1;
    v_rejected := FALSE;
    BEGIN
        INSERT INTO fresh.criterion_scores (id, question_response_id, rubric_criterion_id, points)
        VALUES (gen_random_uuid(), v_response_id, v_criterion_id, 5.01);
    EXCEPTION WHEN SQLSTATE 'P0001' THEN
        GET STACKED DIAGNOSTICS v_message = MESSAGE_TEXT;
        IF v_message NOT LIKE 'Criterion score % exceeds max_points %' THEN
            RAISE EXCEPTION 'Unexpected over-limit score rejection: %', v_message;
        END IF;
        v_rejected := TRUE;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'Score above criterion max_points was not rejected.'; END IF;
END;
$$;

UPDATE fresh.exam_sessions s
SET status = 'Published'
FROM f1a1_test_ids x
WHERE s.id = x.shared_session_id;

INSERT INTO fresh.exam_sessions
    (id, course_id, rubric_mode, rubric_version_id, created_by, title,
     start_time, end_time, time_limit_per_answer_seconds, status)
SELECT invalid_publish_session_id, course_id, 'SharedVersion', version_id, lecturer_id,
       'F1A1 invalid publish verification', now() - interval '1 minute', now() + interval '1 hour', 60, 'Draft'
FROM f1a1_test_ids;

DO $$
DECLARE
    v_session_id uuid;
    v_rejected boolean := FALSE;
    v_message text;
BEGIN
    SELECT invalid_publish_session_id INTO v_session_id FROM f1a1_test_ids;
    BEGIN
        UPDATE fresh.exam_sessions SET status = 'Published' WHERE id = v_session_id;
    EXCEPTION WHEN SQLSTATE '23514' THEN
        GET STACKED DIAGNOSTICS v_message = MESSAGE_TEXT;
        IF position('requires at least one question and participant.' IN v_message) = 0 THEN
            RAISE EXCEPTION 'Unexpected invalid publication rejection: %', v_message;
        END IF;
        v_rejected := TRUE;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'Publishing a SharedVersion session without questions/participants was not rejected.'; END IF;
END;
$$;

SET CONSTRAINTS ALL DEFERRED;

INSERT INTO fresh.exam_sessions
    (id, course_id, rubric_mode, created_by, title, start_time, end_time,
     time_limit_per_answer_seconds, status)
SELECT legacy_session_id, course_id, 'Legacy', lecturer_id, 'F1A1 legacy verification',
       now() - interval '1 minute', now() + interval '1 hour', 60, 'Draft'
FROM f1a1_test_ids;

INSERT INTO fresh.questions (id, exam_session_id, order_no, content)
SELECT legacy_question_id, legacy_session_id, 1, 'F1A1 legacy question'
FROM f1a1_test_ids;

INSERT INTO fresh.rubrics (id, question_id)
SELECT legacy_rubric_id, legacy_question_id FROM f1a1_test_ids;

INSERT INTO fresh.rubric_criteria (id, rubric_id, order_no, description, max_points)
SELECT gen_random_uuid(), legacy_rubric_id, 1, 'Legacy accuracy', 5.00
FROM f1a1_test_ids;

INSERT INTO fresh.participants (id, exam_session_id, student_id)
SELECT legacy_participant_id, legacy_session_id, student_id FROM f1a1_test_ids;

INSERT INTO fresh.interview_attempts (id, participant_id)
SELECT legacy_attempt_id, legacy_participant_id FROM f1a1_test_ids;

INSERT INTO fresh.question_responses (id, attempt_id, question_id)
SELECT legacy_response_id, legacy_attempt_id, legacy_question_id FROM f1a1_test_ids;

INSERT INTO fresh.criterion_scores (id, question_response_id, rubric_criterion_id, points)
SELECT gen_random_uuid(), x.legacy_response_id, c.id, 4.00
FROM f1a1_test_ids x
JOIN fresh.rubric_criteria c ON c.rubric_id = x.legacy_rubric_id;

UPDATE fresh.exam_sessions s
SET status = 'Published'
FROM f1a1_test_ids x
WHERE s.id = x.legacy_session_id;

SET CONSTRAINTS ALL IMMEDIATE;

DO $$
DECLARE
    v_rubric_id uuid;
    v_max_score numeric(5,2);
    v_response_id uuid;
    v_response_score numeric(5,2);
    v_shared_session_id uuid;
    v_expected_version_id uuid;
    v_invalid_publish_session_id uuid;
    v_legacy_session_id uuid;
    v_mode text;
    v_status text;
    v_pinned_version_id uuid;
BEGIN
    SELECT legacy_rubric_id, legacy_response_id INTO v_rubric_id, v_response_id
    FROM f1a1_test_ids;
    SELECT max_score INTO v_max_score FROM fresh.rubrics WHERE id = v_rubric_id;
    SELECT ai_suggested_score INTO v_response_score
    FROM fresh.question_responses WHERE id = v_response_id;
    IF v_max_score IS DISTINCT FROM 5.00::numeric OR v_response_score IS DISTINCT FROM 4.00::numeric THEN
        RAISE EXCEPTION 'Legacy rubric/scoring compatibility failed: max %, response score %.', v_max_score, v_response_score;
    END IF;

    SELECT shared_session_id, version_id, invalid_publish_session_id, legacy_session_id
    INTO v_shared_session_id, v_expected_version_id, v_invalid_publish_session_id, v_legacy_session_id
    FROM f1a1_test_ids;
    SELECT rubric_mode, status, rubric_version_id
    INTO v_mode, v_status, v_pinned_version_id
    FROM fresh.exam_sessions WHERE id = v_shared_session_id;
    IF v_mode IS DISTINCT FROM 'SharedVersion' OR v_status IS DISTINCT FROM 'Published'
       OR v_pinned_version_id IS DISTINCT FROM v_expected_version_id THEN
        RAISE EXCEPTION 'SharedVersion mode, publication, or pinned version check failed.';
    END IF;

    SELECT status INTO v_status FROM fresh.exam_sessions WHERE id = v_invalid_publish_session_id;
    IF v_status IS DISTINCT FROM 'Draft' THEN
        RAISE EXCEPTION 'Failed publication attempt changed the invalid session state.';
    END IF;

    SELECT status INTO v_status FROM fresh.exam_sessions WHERE id = v_legacy_session_id;
    IF v_status IS DISTINCT FROM 'Published' THEN
        RAISE EXCEPTION 'Legacy session publication compatibility check failed.';
    END IF;
END;
$$;

SELECT 'F1-A1 PostgreSQL checks passed; all fixture writes will be rolled back.' AS result;
ROLLBACK;
