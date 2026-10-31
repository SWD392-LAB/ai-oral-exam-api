-- Forward-only upgrade for bounded F3 turn-processing retries and backoff.
-- 01_schema.sql contains the final shape for fresh installs. Run manually after
-- 10_f1_rubric_lifecycle.sql with the psql variable app_schema set.
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
        WHERE relnamespace = v_schema_oid AND relname = 'interview_turns' AND relkind IN ('r', 'p')
    ) THEN
        RAISE EXCEPTION 'Expected interview_turns in application schema %.', v_schema;
    END IF;
    PERFORM set_config('search_path', format('%I, pg_catalog', v_schema), true);
END;
$$;

ALTER TABLE interview_turns
    ADD COLUMN IF NOT EXISTS retry_count INT NOT NULL DEFAULT 0;
ALTER TABLE interview_turns
    ADD COLUMN IF NOT EXISTS next_retry_at TIMESTAMPTZ;

ALTER TABLE interview_turns
    DROP CONSTRAINT IF EXISTS ck_interview_turns_processing_status;
ALTER TABLE interview_turns
    ADD CONSTRAINT ck_interview_turns_processing_status
    CHECK (processing_status IN ('AwaitingAnswer', 'Processing', 'Retry', 'Completed', 'Failed')) NOT VALID;
ALTER TABLE interview_turns
    VALIDATE CONSTRAINT ck_interview_turns_processing_status;

ALTER TABLE interview_turns
    ADD CONSTRAINT ck_interview_turns_retry_count CHECK (retry_count >= 0) NOT VALID;
ALTER TABLE interview_turns
    VALIDATE CONSTRAINT ck_interview_turns_retry_count;

CREATE INDEX IF NOT EXISTS ix_interview_turns_retry_due
    ON interview_turns(processing_status, next_retry_at)
    WHERE processing_status = 'Retry';

COMMIT;
