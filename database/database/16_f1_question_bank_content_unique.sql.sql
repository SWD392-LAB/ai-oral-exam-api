-- Reconcile legacy question-bank hashes with the canonical content hash used by the application
-- and database/01_schema.sql. Whitespace is collapsed and content is case-insensitive.
BEGIN;

SELECT set_config('aives.application_schema', :'app_schema', true);

DO $$
DECLARE
    v_schema text := NULLIF(current_setting('aives.application_schema', true), '');
    v_schema_oid oid;
    v_collisions text;
BEGIN
    IF v_schema IS NULL THEN
        RAISE EXCEPTION 'Set the required psql variable app_schema to the application schema and retry.';
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
        WHERE relnamespace = v_schema_oid AND relname = 'question_bank_items' AND relkind IN ('r', 'p')
    ) THEN
        RAISE EXCEPTION 'Expected question_bank_items in application schema %.', v_schema;
    END IF;
    PERFORM set_config('search_path', format('%I, pg_catalog', v_schema), true);

    EXECUTE format($sql$
        SELECT string_agg(format('course=%%s item_ids=%%s', course_id, item_ids), '; ')
        FROM (
            SELECT course_id, array_agg(id ORDER BY id) AS item_ids
            FROM question_bank_items
            GROUP BY course_id,
                encode(sha256(convert_to(upper(btrim(regexp_replace(content, '\s+', ' ', 'g'))), 'UTF8')), 'hex')
            HAVING count(*) > 1
            ORDER BY course_id
            LIMIT 20
        ) collisions
    $sql$)
    INTO v_collisions;

    IF v_collisions IS NOT NULL THEN
        RAISE EXCEPTION 'Resolve existing canonical question-bank content duplicates before applying this migration. Sample collision groups: %',
            v_collisions;
    END IF;

    EXECUTE format('DROP INDEX IF EXISTS %I.ux_question_bank_course_normalized_content', v_schema);
    EXECUTE format('ALTER TABLE %I.question_bank_items DROP COLUMN IF EXISTS normalized_content', v_schema);

    -- Prevent transient unique-index conflicts while replacing legacy hashes.
    EXECUTE format('DROP INDEX IF EXISTS %I.ux_question_bank_course_content_hash', v_schema);
    UPDATE question_bank_items
       SET content_hash = encode(sha256(convert_to(upper(btrim(regexp_replace(content, '\s+', ' ', 'g'))), 'UTF8')), 'hex')
     WHERE content_hash IS DISTINCT FROM
        encode(sha256(convert_to(upper(btrim(regexp_replace(content, '\s+', ' ', 'g'))), 'UTF8')), 'hex');
    CREATE UNIQUE INDEX IF NOT EXISTS ux_question_bank_course_content_hash
        ON question_bank_items(course_id, content_hash);
END;
$$;

COMMIT;
