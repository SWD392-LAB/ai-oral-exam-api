-- Allow attempts with terminal turn-processing failures to close without entering review.
-- Run manually after 11_f3_turn_retry_backoff.sql with the psql variable app_schema set.
-- This script is source-only; never apply it automatically to a database.
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
        WHERE relnamespace = v_schema_oid AND relname = 'interview_attempts' AND relkind IN ('r', 'p')
    ) THEN
        RAISE EXCEPTION 'Expected interview_attempts in application schema %.', v_schema;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_class
        WHERE relnamespace = v_schema_oid AND relname = 'participants' AND relkind IN ('r', 'p')
    ) OR NOT EXISTS (
        SELECT 1 FROM pg_class
        WHERE relnamespace = v_schema_oid AND relname = 'question_responses' AND relkind IN ('r', 'p')
    ) OR NOT EXISTS (
        SELECT 1 FROM pg_class
        WHERE relnamespace = v_schema_oid AND relname = 'interview_turns' AND relkind IN ('r', 'p')
    ) THEN
        RAISE EXCEPTION 'Expected participants, question_responses, and interview_turns in application schema %.', v_schema;
    END IF;
    PERFORM set_config('search_path', format('%I, pg_catalog', v_schema), true);
END;
$$;

ALTER TABLE interview_attempts
    DROP CONSTRAINT IF EXISTS interview_attempts_status_check;
ALTER TABLE interview_attempts
    DROP CONSTRAINT IF EXISTS ck_interview_attempts_status;
ALTER TABLE interview_attempts
    ADD CONSTRAINT ck_interview_attempts_status
    CHECK (status IN ('InProgress', 'Interrupted', 'PendingReview', 'Finalized')) NOT VALID;
ALTER TABLE interview_attempts
    VALIDATE CONSTRAINT ck_interview_attempts_status;

UPDATE interview_attempts AS attempt
SET status = 'Interrupted',
    completed_at = GREATEST(COALESCE(attempt.completed_at, now()), attempt.started_at)
WHERE attempt.status = 'InProgress'
  AND EXISTS (
      SELECT 1
      FROM question_responses AS response
      JOIN interview_turns AS turn ON turn.question_response_id = response.id
      WHERE response.attempt_id = attempt.id
        AND turn.processing_status = 'Failed'
  );

UPDATE participants AS participant
SET status = CASE
    WHEN EXISTS (
      SELECT 1
      FROM interview_attempts AS attempt
      WHERE attempt.participant_id = participant.id
         AND attempt.status = 'InProgress'
    ) THEN 'InProgress'
    WHEN EXISTS (
      SELECT 1
      FROM interview_attempts AS attempt
      WHERE attempt.participant_id = participant.id
         AND attempt.status IN ('PendingReview', 'Finalized')
    ) THEN 'Completed'
    ELSE 'Registered'
END
WHERE EXISTS (
    SELECT 1
    FROM interview_attempts AS attempt
    WHERE attempt.participant_id = participant.id
      AND attempt.status = 'Interrupted'
)
AND participant.status IS DISTINCT FROM CASE
    WHEN EXISTS (
        SELECT 1
        FROM interview_attempts AS attempt
        WHERE attempt.participant_id = participant.id
          AND attempt.status = 'InProgress'
    ) THEN 'InProgress'
    WHEN EXISTS (
        SELECT 1
        FROM interview_attempts AS attempt
        WHERE attempt.participant_id = participant.id
          AND attempt.status IN ('PendingReview', 'Finalized')
    ) THEN 'Completed'
    ELSE 'Registered'
END;

COMMIT;
