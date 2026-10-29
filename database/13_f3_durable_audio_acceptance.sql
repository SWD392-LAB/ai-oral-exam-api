-- Persist trusted audio acceptance separately from the answer/transcript timestamp.
-- Run manually after 12_f3_interrupted_attempts.sql with the psql variable app_schema set.
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
    ADD COLUMN IF NOT EXISTS audio_completion_received_at TIMESTAMPTZ;
ALTER TABLE interview_turns
    ADD COLUMN IF NOT EXISTS audio_accepted_at TIMESTAMPTZ;
ALTER TABLE interview_turns
    ADD COLUMN IF NOT EXISTS transcription_retry_count INT NOT NULL DEFAULT 0;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'interview_turns'::regclass
          AND conname = 'ck_interview_turns_transcription_retry_count'
    ) THEN
        ALTER TABLE interview_turns
            ADD CONSTRAINT ck_interview_turns_transcription_retry_count
            CHECK (transcription_retry_count >= 0) NOT VALID;
    END IF;
END;
$$;

ALTER TABLE interview_turns
    VALIDATE CONSTRAINT ck_interview_turns_transcription_retry_count;

COMMIT;
