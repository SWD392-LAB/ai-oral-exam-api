Round 6 re-review — Request Changes
Reviewed head: d91aa2ef0d044827afc17872081812cc09b84844 against develop.

The previous schema-parity finding is addressed: migration 13 now recomputes the canonical hash already used by the fresh schema, and CI checks the legacy upgrade path. The new PostgreSQL/Keycloak script also exercises real student and lecturer tokens, a published text attempt through review/feedback/statistics/CSV, mock voice upload/transcription, retry, authorization failures, timeout, and API restart recovery. GitHub reports all four checks green and the branch mergeable.

Local Docker verification
Full solution: 366 unit and 52 integration tests passed; 0 failed (11 unit and 13 integration tests skipped).
Migration 13 duplicate-detection and legacy-hash upgrade checks passed.
Role-authenticated E2E passed against PostgreSQL 16 and Keycloak 26.8 with jq 1.7; PostgreSQL-filtered tests passed (6 passed, 5 skipped).
In the Debian-based .NET SDK Docker image, the E2E script fails with jq 1.6 at create_exam: $end is parsed as jq's end keyword. It passes with the Ubuntu SDK image's jq 1.7, so the script has an unpinned runtime compatibility issue.
Remaining findings
1 The PostgreSQL E2E bypasses F1 question-bank create/import, approval, and attach by creating questions directly on the session. The existing HTTP question-bank tests still use EF InMemory; the PostgreSQL tests call services directly. TASK-K's bank/attach contract is therefore not exercised end-to-end against PostgreSQL/Keycloak.
2 Migration 13 lacks the schema/table validation used by the neighboring upgrade scripts, and the work plan assigns database/ ownership to Minh. Please coordinate this data-rewriting migration with him.
3 The E2E jq command is not portable to jq 1.6, which is installed by the Debian .NET SDK image used for local Docker runs.
4 This PR still includes unused GeminiAnswerAnalyzer/GeminiRubricScorer F3 adapters. InterviewModule registers the generic LlmAnswerAnalyzer/LlmRubricScorer instead; F3 AI implementation belongs to Phúc/Minh under the work plan.

The inline comments give concrete fixes for each item. I am keeping this review at Request Changes until these remaining contract, ownership, and test-runner issues are addressed.

.github/scripts/postgres-keycloak-api-e2e.sh 192-195

[P2] Exercise the F1 bank and attach path in the PostgreSQL E2E. create_exam inserts the question directly through /api/exam-sessions/{id}/questions; it never calls the question-bank create/import, approve, or /questions/from-bank routes. The HTTP question-bank contracts still run against EF InMemory, while PostgreSQL tests call the service directly. As a result, this E2E can pass even if the real PostgreSQL/Keycloak bank-to-session contract is broken. Please create/import a Draft through the bank API, approve it, attach it to the Draft session, and assert the attached source revision/snapshot before publishing.

database/13_f1_question_bank_content_unique.sql 9-12

[P2] Validate the selected application schema before rewriting its hashes. This migration only checks that app_schema is non-null; unlike migrations 11, 12, and 14, it does not verify that the schema exists or contains the expected question_bank_items table. A mistaken but valid schema name can make the subsequent DROP INDEX / UPDATE / CREATE INDEX operate on the wrong bank table. Please add the schema and relation checks used by the neighboring migrations. The work plan also assigns database/ and migrations to Minh; please have him own/review this change while Khang supplies the bank contract and tests.

.github/scripts/postgres-keycloak-api-e2e.sh

[P2] Avoid the jq 1.6 reserved variable name end. This command passes --arg end and reads $end; in the Debian-based mcr.microsoft.com/dotnet/sdk:8.0 container with jq 1.6, jq errors with unexpected end at this filter and the E2E then sends an empty session payload. I reproduced that failure locally. The current Ubuntu GitHub runner has jq 1.8 and passes, and the rerun with jq 1.7 passed. Rename the variable to end_at (including the filter reference), or pin/check the required jq version so the documented local Docker workflow is reproducible.

src/Modules/Interview/AiOralExam.Modules.Interview/Application/Ai/GeminiAnswerAnalyzer.cs

[P2] Keep F3 AI implementation out of TASK-K's bank/CI PR. This Gemini analyzer and GeminiRubricScorer have no registrations or call sites; InterviewModule resolves LlmAnswerAnalyzer and LlmRubricScorer instead. The work plan assigns Interview/Application/Ai to Phúc and says Khang tests F3 rather than implementing its business logic. Please remove these unused duplicate adapters from this PR or move the intended implementation to Phúc's owned change.

