-- Forward-only upgrade for claim-token protection during recovered F3 turn processing.
-- 01_schema.sql contains the final shape for fresh installs. Run manually after 07_f3_turn_processing.sql
-- with psql variable app_schema set; do not apply automatically to any database.
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
    IF v_schema = 'information_schema' OR v_schema ~ '^pg_' THEN
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

ALTER TABLE interview_turns ADD COLUMN IF NOT EXISTS processing_token UUID;
UPDATE interview_turns
SET processing_token = gen_random_uuid()
WHERE processing_status = 'Processing' AND processing_token IS NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_interview_turns_processing_token'
          AND conrelid = 'interview_turns'::regclass
    ) THEN
        ALTER TABLE interview_turns
            ADD CONSTRAINT ck_interview_turns_processing_token
            CHECK ((processing_status = 'Processing') = (processing_token IS NOT NULL)) NOT VALID;
    END IF;
END;
$$;

ALTER TABLE interview_turns VALIDATE CONSTRAINT ck_interview_turns_processing_token;

COMMIT;
