-- Reconcile legacy question-bank hashes with the canonical content hash used by the application
-- and database/01_schema.sql. Whitespace is collapsed and content is case-insensitive.
BEGIN;

SELECT set_config('aives.application_schema', :'app_schema', true);

DO $$
DECLARE
    v_schema text := NULLIF(current_setting('aives.application_schema', true), '');
    v_collisions text;
BEGIN
    IF v_schema IS NULL THEN
        RAISE EXCEPTION 'Set the required psql variable app_schema to the application schema and retry.';
    END IF;

    EXECUTE format($sql$
        SELECT string_agg(format('course=%%s item_ids=%%s', course_id, item_ids), '; ')
        FROM (
            SELECT course_id, array_agg(id ORDER BY id) AS item_ids
            FROM %I.question_bank_items
            GROUP BY course_id,
                encode(sha256(convert_to(upper(btrim(regexp_replace(content, '\s+', ' ', 'g'))), 'UTF8')), 'hex')
            HAVING count(*) > 1
            ORDER BY course_id
            LIMIT 20
        ) collisions
    $sql$, v_schema)
    INTO v_collisions;

    IF v_collisions IS NOT NULL THEN
        RAISE EXCEPTION 'Resolve existing canonical question-bank content duplicates before applying this migration. Sample collision groups: %',
            v_collisions;
    END IF;

    EXECUTE format('DROP INDEX IF EXISTS %I.ux_question_bank_course_normalized_content', v_schema);
    EXECUTE format('ALTER TABLE %I.question_bank_items DROP COLUMN IF EXISTS normalized_content', v_schema);

    -- Prevent transient unique-index conflicts while replacing legacy hashes.
    EXECUTE format('DROP INDEX IF EXISTS %I.ux_question_bank_course_content_hash', v_schema);
    EXECUTE format($sql$
        UPDATE %I.question_bank_items
           SET content_hash = encode(sha256(convert_to(upper(btrim(regexp_replace(content, '\s+', ' ', 'g'))), 'UTF8')), 'hex')
         WHERE content_hash IS DISTINCT FROM
            encode(sha256(convert_to(upper(btrim(regexp_replace(content, '\s+', ' ', 'g'))), 'UTF8')), 'hex')
    $sql$, v_schema);
    EXECUTE format(
        'CREATE UNIQUE INDEX IF NOT EXISTS ux_question_bank_course_content_hash ON %I.question_bank_items(course_id, content_hash)',
        v_schema);
END;
$$;

COMMIT;
