Round 5 review — Request Changes
Reviewed head: aeeb3c74506fba3f98bc131afd3c5aff3d459bb6 against develop.

Resolved from the previous review
The row-specific import duplicate errors, compact question-bank list DTO, EF-safe attach filter, migration format() fix, and PostgreSQL conflict mapping are present. They passed the local checks listed below. The branch is mergeable, and all four GitHub Actions checks are green.

Remaining blockers
Fresh-install and upgrade schemas differ. PostgreSQL 16 initialization from database/01_schema.sql does not create normalized_content; I verified the column-existence query returns false. Migration 13 adds the column and unique index for upgraded databases. The CI job creates a fresh schema and then applies migration 13, so it does not validate a pristine install. Please use one canonical uniqueness rule across 01_schema.sql and migration 13, and add a clean-install parity check. Details are on the inline comment at database/13_f1_question_bank_content_unique.sql:34.

TASK-K's required end-to-end workflow is missing. The new PostgreSQL/Keycloak smoke script validates a service-account token with only GET /api/auth/me; it does not verify student/lecturer role behavior or the required publish → exam → lecturer review → student feedback → statistics/CSV flow. The question-bank HTTP contracts still use EF InMemory, and the PostgreSQL tests call services directly. Please add the role-authenticated E2E harness and coordinate the voice/reconnect and F3 steps with Minh/Phúc. Details are on the inline comment at .github/workflows/backend-ci.yml:113.

Local validation
In disposable Docker containers: the solution suite passed (366 unit, 52 integration; 0 failures; 11 unit and 13 integration skipped); PostgreSQL-filtered tests passed (6 passed, 5 skipped); migration 13 rejected a duplicate legacy fixture and then applied successfully; and a Keycloak-issued token authenticated against the PostgreSQL-backed API. These results verify the covered contracts but do not satisfy the missing full E2E acceptance.

database/13_f1_question_bank_content_unique.sql lines 31-34
[P2] Align the fresh-install schema with migration 13.

Reproduction: create a clean PostgreSQL 16 database/schema and run only database/01_schema.sql; then query information_schema.columns for question_bank_items.normalized_content. I ran this and the result was false. The same file does not define ux_question_bank_course_normalized_content.

Impact: existing databases that apply migration 13 receive this extra generated column and unique index, while clean installs do not. The CI job initializes from 01_schema.sql and immediately applies migration 13, so it masks the difference. The migration header also claims fresh installs already receive these objects, which is not true on this head.

Requested fix: choose one canonical uniqueness definition and make fresh schema and upgrade migration produce the same result. Either include the generated column/index in 01_schema.sql and test a pristine install, or remove this migration-only rule and rely on the existing canonical content-hash constraint. Please also reconcile this lower(btrim(content)) key with the application hash's whitespace normalization so fresh and upgraded databases enforce the same contract.

.github/workflows/backend-ci.yml lines 110-113

[P2] Cover the TASK-K publish-to-report acceptance flow.

The new script obtains a Keycloak service-account token and makes one authenticated request: GET /api/auth/me. That proves token validation and user lookup, but it does not exercise student/lecturer roles, publishing a session, taking an exam, lecturer review, student feedback, or statistics/CSV. The question-bank HTTP contract tests use EF InMemory, and the PostgreSQL-filtered tests call services directly.

Requested fix: add a disposable PostgreSQL/Keycloak E2E scenario with role-specific student and lecturer identities that follows the work plan through publish → text/voice exam → lecturer review → student feedback → statistics/CSV. Include the required permission, timeout/retry, reconnect, and worker-restart cases; coordinate provider-dependent voice and interview/review steps with Minh/Phúc. Until that scenario runs in CI, TASK-K's stated E2E acceptance is still open.

