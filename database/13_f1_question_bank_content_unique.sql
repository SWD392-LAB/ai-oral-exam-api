-- Forward-only upgrade for existing databases. Fresh databases get these objects from 01_schema.sql.
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
            GROUP BY course_id, lower(btrim(content))
            HAVING count(*) > 1
            ORDER BY course_id
            LIMIT 20
        ) collisions
    $sql$, v_schema)
    INTO v_collisions;

    IF v_collisions IS NOT NULL THEN
        RAISE EXCEPTION 'Resolve existing normalized question-bank content duplicates before applying this migration. Sample collision groups: %',
            v_collisions;
    END IF;

    EXECUTE format(
        'ALTER TABLE %I.question_bank_items ADD COLUMN IF NOT EXISTS normalized_content TEXT GENERATED ALWAYS AS (lower(btrim(content))) STORED',
        v_schema);
    EXECUTE format(
        'CREATE UNIQUE INDEX IF NOT EXISTS ux_question_bank_course_normalized_content ON %I.question_bank_items(course_id, normalized_content)',
        v_schema);
END;
$$;

COMMIT;
