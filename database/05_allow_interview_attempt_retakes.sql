-- Forward-only upgrade for existing databases. Fresh databases get this from 01_schema.sql.
BEGIN;

SELECT set_config('aives.application_schema', :'app_schema', true);

DO $$
DECLARE
    v_schema text := NULLIF(current_setting('aives.application_schema', true), '');
    v_schema_oid oid;
    v_table oid;
    v_schema_table_count integer;
    v_participant_attnum smallint;
    v_unique_constraint_count integer;
    v_constraint_name name;
    v_nonunique_index_exists boolean;
    v_index_name_exists boolean;
    v_named_index_matches boolean;
BEGIN
    IF v_schema IS NULL THEN
        RAISE EXCEPTION 'Set the required psql variable app_schema to the application schema and retry.';
    END IF;

    IF v_schema = 'information_schema' OR v_schema ~ '^pg_' THEN
        RAISE EXCEPTION 'Schema % is not an application schema.', v_schema;
    END IF;

    SELECT n.oid
    INTO v_schema_oid
    FROM pg_namespace n
    WHERE n.nspname = v_schema;

    IF v_schema_oid IS NULL THEN
        RAISE EXCEPTION 'Selected application schema % does not exist.', v_schema;
    END IF;

    SELECT count(*)
    INTO v_schema_table_count
    FROM pg_class t
    WHERE t.relname = 'interview_attempts'
      AND t.relkind IN ('r', 'p')
      AND t.relnamespace = v_schema_oid;

    IF v_schema_table_count <> 1 THEN
        RAISE EXCEPTION 'Expected one interview_attempts table in selected schema %; found %.',
            v_schema, v_schema_table_count;
    END IF;

    SELECT t.oid
    INTO v_table
    FROM pg_class t
    WHERE t.relname = 'interview_attempts'
      AND t.relkind IN ('r', 'p')
      AND t.relnamespace = v_schema_oid;

    SELECT a.attnum
    INTO v_participant_attnum
    FROM pg_attribute a
    WHERE a.attrelid = v_table
      AND a.attname = 'participant_id'
      AND a.attnum > 0
      AND NOT a.attisdropped
      AND a.attnotnull;

    IF v_participant_attnum IS NULL THEN
        RAISE EXCEPTION 'Expected interview_attempts.participant_id to exist and be NOT NULL.';
    END IF;

    SELECT count(*)
    INTO v_unique_constraint_count
    FROM pg_constraint c
    WHERE c.conrelid = v_table
      AND c.contype = 'u'
      AND c.conkey = ARRAY[v_participant_attnum]::smallint[];

    IF v_unique_constraint_count > 1 THEN
        RAISE EXCEPTION 'Multiple single-column UNIQUE constraints exist on participant_id; inspect manually.';
    ELSIF v_unique_constraint_count = 1 THEN
        SELECT c.conname
        INTO v_constraint_name
        FROM pg_constraint c
        WHERE c.conrelid = v_table
          AND c.contype = 'u'
          AND c.conkey = ARRAY[v_participant_attnum]::smallint[];

        EXECUTE format('ALTER TABLE %I.%I DROP CONSTRAINT %I',
            v_schema, 'interview_attempts', v_constraint_name);
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_index i
        WHERE i.indrelid = v_table
          AND i.indisunique
          AND i.indnkeyatts = 1
          AND i.indkey[0] = v_participant_attnum
    ) THEN
        RAISE EXCEPTION 'A standalone UNIQUE index on participant_id remains; inspect it manually.';
    END IF;

    SELECT EXISTS (
        SELECT 1
        FROM pg_index i
        JOIN pg_class ix ON ix.oid = i.indexrelid
        JOIN pg_am am ON am.oid = ix.relam
        WHERE i.indrelid = v_table
          AND NOT i.indisunique
          AND i.indisvalid
          AND i.indisready
          AND i.indpred IS NULL
          AND i.indexprs IS NULL
          AND i.indnkeyatts = 1
          AND i.indkey[0] = v_participant_attnum
          AND am.amname = 'btree'
    ) INTO v_nonunique_index_exists;

    SELECT EXISTS (
        SELECT 1
        FROM pg_class ix
        WHERE ix.relnamespace = v_schema_oid
          AND ix.relname = 'ix_interview_attempts_participant'
    ) INTO v_index_name_exists;

    SELECT EXISTS (
        SELECT 1
        FROM pg_index i
        JOIN pg_class ix ON ix.oid = i.indexrelid
        JOIN pg_am am ON am.oid = ix.relam
        WHERE i.indrelid = v_table
          AND ix.relnamespace = v_schema_oid
          AND ix.relname = 'ix_interview_attempts_participant'
          AND NOT i.indisunique
          AND i.indisvalid
          AND i.indisready
          AND i.indpred IS NULL
          AND i.indexprs IS NULL
          AND i.indnkeyatts = 1
          AND i.indkey[0] = v_participant_attnum
          AND am.amname = 'btree'
    ) INTO v_named_index_matches;

    IF v_index_name_exists AND NOT v_named_index_matches THEN
        IF v_nonunique_index_exists THEN
            RAISE NOTICE 'Index name ix_interview_attempts_participant conflicts with another object in schema %, but an equivalent index already exists; no index was created.', v_schema;
        ELSE
            RAISE EXCEPTION 'Index name ix_interview_attempts_participant already exists with a different definition in schema %; no changes will be committed.', v_schema;
        END IF;
    END IF;

    IF NOT v_nonunique_index_exists THEN
        EXECUTE format('CREATE INDEX %I ON %I.%I USING btree (%I)',
            'ix_interview_attempts_participant',
            v_schema, 'interview_attempts', 'participant_id');
    END IF;
END;
$$;

COMMIT;
