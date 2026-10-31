-- Forward-only upgrade for PR #8 Round 2 review findings.
-- Fresh databases get the claim column and named unique constraint from 01_schema.sql.
-- Run manually with psql variable app_schema set; this script never selects a database.
BEGIN;

SELECT set_config('aives.application_schema', :'app_schema', true);

DO $$
DECLARE
    v_schema text := NULLIF(current_setting('aives.application_schema', true), '');
    v_has_duplicates boolean;
    v_has_unique_key boolean;
BEGIN
    IF v_schema IS NULL THEN
        RAISE EXCEPTION 'Set the required psql variable app_schema and retry.';
    END IF;
    IF v_schema = 'information_schema' OR v_schema ~ '^pg_' THEN
        RAISE EXCEPTION 'Schema % is not an application schema.', v_schema;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = v_schema) THEN
        RAISE EXCEPTION 'Selected application schema % does not exist.', v_schema;
    END IF;

    EXECUTE format('ALTER TABLE %I.question_responses ADD COLUMN IF NOT EXISTS ai_evaluation_claim_token UUID', v_schema);

    EXECUTE format($sql$
        SELECT EXISTS (
            SELECT 1 FROM %I.criterion_scores
            GROUP BY question_response_id, rubric_criterion_id
            HAVING count(*) > 1
        )
    $sql$, v_schema) INTO v_has_duplicates;
    IF v_has_duplicates THEN
        RAISE EXCEPTION 'Duplicate criterion scores exist; resolve them before adding the unique response/criterion key.';
    END IF;

    SELECT EXISTS (
        SELECT 1
          FROM pg_index i
          JOIN pg_class t ON t.oid = i.indrelid
          JOIN pg_namespace n ON n.oid = t.relnamespace
          JOIN pg_attribute response_col ON response_col.attrelid = t.oid AND response_col.attname = 'question_response_id'
          JOIN pg_attribute criterion_col ON criterion_col.attrelid = t.oid AND criterion_col.attname = 'rubric_criterion_id'
         WHERE n.nspname = v_schema
           AND t.relname = 'criterion_scores'
           AND i.indisunique
           AND i.indnkeyatts = 2
           AND i.indkey[0] = response_col.attnum
           AND i.indkey[1] = criterion_col.attnum
    ) INTO v_has_unique_key;
    IF NOT v_has_unique_key THEN
        EXECUTE format(
            'CREATE UNIQUE INDEX ux_criterion_scores_response_criterion ON %I.criterion_scores(question_response_id, rubric_criterion_id)',
            v_schema);
    END IF;
END;
$$;

COMMIT;
