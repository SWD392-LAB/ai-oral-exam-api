-- Persist whether an attempt was validly completed with unasked session questions.
-- Run manually after 14_f3_manual_turn_recovery.sql with the psql variable app_schema set.
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
    PERFORM set_config('search_path', format('%I, pg_catalog', v_schema), true);
END;
$$;

ALTER TABLE interview_attempts
    ADD COLUMN IF NOT EXISTS partial_completed BOOLEAN NOT NULL DEFAULT FALSE;

COMMIT;
