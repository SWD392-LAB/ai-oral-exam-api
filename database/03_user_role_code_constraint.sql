-- Upgrade existing databases. Fresh installs get this trigger from 01_schema.sql.
BEGIN;

DO $$
DECLARE
    violating_users TEXT;
BEGIN
    SELECT string_agg(
        format('id=%s, email=%s, role=%s, student_code=%s, lecturer_code=%s',
            u.id, u.email, r.name, u.student_code, u.lecturer_code),
        E'\n' ORDER BY u.email)
    INTO violating_users
    FROM users u
    JOIN roles r ON r.id = u.role_id
    WHERE NOT (
        (r.name = 'Student' AND u.student_code IS NOT NULL AND btrim(u.student_code) <> '' AND u.lecturer_code IS NULL)
        OR (r.name = 'Lecturer' AND u.lecturer_code IS NOT NULL AND btrim(u.lecturer_code) <> '' AND u.student_code IS NULL)
        OR (r.name = 'Administrator' AND u.student_code IS NULL AND u.lecturer_code IS NULL)
    );

    IF violating_users IS NOT NULL THEN
        RAISE EXCEPTION 'Existing users violate the role/code requirements:%', E'\n' || violating_users
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_users_role_codes';
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION fn_users_check_role_codes() RETURNS TRIGGER AS $$
DECLARE
    v_role_name roles.name%TYPE;
BEGIN
    SELECT r.name INTO v_role_name
    FROM roles r
    WHERE r.id = NEW.role_id;

    IF NOT FOUND THEN
        RETURN NEW;
    END IF;

    IF v_role_name = 'Student' THEN
        IF NEW.student_code IS NULL OR btrim(NEW.student_code) = '' OR NEW.lecturer_code IS NOT NULL THEN
            RAISE EXCEPTION 'Student user % must have a non-blank student_code and no lecturer_code.', NEW.id
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_users_role_codes';
        END IF;
    ELSIF v_role_name = 'Lecturer' THEN
        IF NEW.lecturer_code IS NULL OR btrim(NEW.lecturer_code) = '' OR NEW.student_code IS NOT NULL THEN
            RAISE EXCEPTION 'Lecturer user % must have a non-blank lecturer_code and no student_code.', NEW.id
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_users_role_codes';
        END IF;
    ELSIF v_role_name = 'Administrator' THEN
        IF NEW.student_code IS NOT NULL OR NEW.lecturer_code IS NOT NULL THEN
            RAISE EXCEPTION 'Administrator user % must not have student_code or lecturer_code.', NEW.id
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_users_role_codes';
        END IF;
    ELSE
        RAISE EXCEPTION 'User % has an unsupported role for role-code validation.', NEW.id
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_users_role_codes';
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS ck_users_role_codes ON users;
CREATE TRIGGER ck_users_role_codes
BEFORE INSERT OR UPDATE ON users
FOR EACH ROW EXECUTE FUNCTION fn_users_check_role_codes();

COMMIT;
