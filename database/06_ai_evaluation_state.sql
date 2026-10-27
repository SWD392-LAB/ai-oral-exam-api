-- Forward-only upgrade for existing databases. Fresh databases get these columns from 01_schema.sql.
BEGIN;

SELECT set_config('aives.application_schema', :'app_schema', true);

DO $$
DECLARE
    v_schema text := NULLIF(current_setting('aives.application_schema', true), '');
BEGIN
    IF v_schema IS NULL THEN
        RAISE EXCEPTION 'Set the required psql variable app_schema to the application schema and retry.';
    END IF;

    EXECUTE format('ALTER TABLE %I.question_responses ADD COLUMN IF NOT EXISTS ai_evaluation_state VARCHAR(20)', v_schema);
    EXECUTE format('ALTER TABLE %I.question_responses ADD COLUMN IF NOT EXISTS ai_evaluation_error VARCHAR(500)', v_schema);
    EXECUTE format($sql$
        UPDATE %I.question_responses
           SET ai_evaluation_state = CASE
               WHEN is_finished THEN 'Completed'
               WHEN EXISTS (
                   SELECT 1 FROM %I.interview_turns t
                   WHERE t.question_response_id = question_responses.id
                     AND t.answer_transcript IS NOT NULL
                     AND t.answered_at IS NULL
               ) THEN 'Failed'
               ELSE 'NotStarted'
           END
         WHERE ai_evaluation_state IS NULL
    $sql$, v_schema, v_schema);
    EXECUTE format('ALTER TABLE %I.question_responses ALTER COLUMN ai_evaluation_state SET DEFAULT ''NotStarted''', v_schema);
    EXECUTE format('ALTER TABLE %I.question_responses ALTER COLUMN ai_evaluation_state SET NOT NULL', v_schema);
    EXECUTE format($sql$
        ALTER TABLE %I.question_responses
        ADD CONSTRAINT ck_question_response_ai_state
        CHECK (ai_evaluation_state IN ('NotStarted', 'Pending', 'Failed', 'Completed'))
    $sql$, v_schema);
EXCEPTION
    WHEN duplicate_object THEN NULL;
END;
$$;

COMMIT;
