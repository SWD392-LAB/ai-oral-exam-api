# ai-oral-exam-api

Backend of **AIVES – AI-powered Viva Exam System** (SWD392). An AI examiner asks the questions, asks follow-ups and suggests a score against the rubric. The lecturer always confirms the final score.

- **Architecture:** ASP.NET Core 8 **modular monolith** – one API, three business modules, one database
- **Database:** PostgreSQL 16, accessed with EF Core 8 (Npgsql). The schema is created by **SQL scripts** (`database/`), *not* by EF migrations
- **Auth:** handled by **Keycloak** (realm `aives`, see [keycloak/README.md](keycloak/README.md)). The API stores no passwords; it only validates Keycloak access tokens. On every request it then reads `users.is_active` and `users.role_id` (`Student` / `Lecturer` / `Administrator`): the database decides access, so deactivating a user or changing a role in AIVES applies at once
- **AI:** STT / TTS / LLM behind interfaces. M1 uses **mocks** (deterministic scoring: same input, same result)

Frontend repo: `ai-oral-exam-web`.

---

## 1. Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 8.0.x | `dotnet --version` |
| Docker Desktop | recent | `docker compose version` |
| (optional) Visual Studio 2022 / Rider / VS Code + C# Dev Kit | | |

> You do not need PostgreSQL installed locally: Docker runs it for you.

## 2. Quick start (4 steps)

```bash
# 1) Get the code
git clone https://github.com/SWD392-LAB/ai-oral-exam-api.git
cd ai-oral-exam-api

# 2) Start the database + Keycloak (the first start runs database/01_schema.sql, then 02_seed.sql,
#    and imports keycloak/aives-realm.json)
docker compose up -d
docker compose ps          # wait until postgres is "healthy" and keycloak is up (http://localhost:8080)

# 3) Run the API
dotnet run --project src/AiOralExam.Api --launch-profile http

# 4) Open Swagger
#    http://localhost:5080/swagger
```

Check that the API can reach the database: open http://localhost:5080/health. It should say `Healthy`.

`database/01_schema.sql` is the final fresh-install schema. Docker runs it followed by
`database/02_seed.sql`; it includes the current EF columns, including durable audio
acceptance, transcription-retry, and manual-recovery metadata.

For an existing database, apply the forward-only scripts in numeric order from `03` through
`14`. Do not rerun `database/01_schema.sql` against existing data. Scripts `03` and `04`
use the session `search_path`, so set it to the application schema before running them.
Scripts `05` through `14` additionally require the psql variable `app_schema`; each script
validates that schema and sets its own session `search_path` to `<app_schema>, pg_catalog`.
The guarded application-schema migrations reject unsafe schemas such as
`information_schema` and `pg_*`; migration `14` explicitly rejects `public`. Migration `14` adds the bounded
`manual_recovery_count` column and constraint required for one manual interrupted-turn
recovery; it must run after migration `13` and requires an explicitly supplied non-public
`app_schema`.

For example, with an application schema named `aives_app`:

```bash
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 \
  -c 'SET search_path TO aives_app, pg_catalog' \
  -f database/03_user_role_code_constraint.sql
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 \
  -c 'SET search_path TO aives_app, pg_catalog' \
  -f database/04_audit_log_description.sql
for script in database/{05..14}_*.sql; do
  psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -v app_schema=aives_app -f "$script"
done
```

The ordered upgrades are: role/code validation (`03`), audit and retake changes (`04`-`05`),
rubric changes (`06`, `08`, and `10`), and F3 processing, lease, retry, interrupted-attempt,
and durable-audio changes (`07`, `09`, and `11`-`13`), followed by manual interrupted-turn
recovery (`14`). Migrations `13` and `14` remain compatible with the final `01_schema.sql`
because they use guarded column/constraint operations; both require a non-public `app_schema`.

### Isolated PostgreSQL test database

The PostgreSQL recovery tests are opt-in. Create a fresh, dedicated database and set
`AIVES_POSTGRES_TEST_CONNECTION_STRING` to it; never use the development database `aives`.
Use a disposable database per test run and do not share it with the API, development tools,
or another test process. Prepare it from the fresh-install schema without running any upgrade
migrations:

```powershell
$env:AIVES_POSTGRES_TEST_CONNECTION_STRING = "Host=localhost;Port=5432;Database=r3_phase2_test;Username=<test-role>;Password=<test-password>;Search Path=r3_phase2_app,pg_catalog"
dotnet test tests/AiOralExam.IntegrationTests/AiOralExam.IntegrationTests.csproj
Remove-Item Env:AIVES_POSTGRES_TEST_CONNECTION_STRING
```

The database must be exactly `r3_phase2_test` for the focused Round 3 recovery tests, and the
search path must include the non-public `r3_phase2_app` schema and must not use `public`.
Verify the connection string before starting the tests. The tests create uniquely named rows
but do not clean them up; isolate each run and drop only this dedicated test database manually
after the run. No test setup should execute migrations against an existing development or
production database. The successful manual-recovery audit integration test is implemented and
build-verified, but is not considered executed unless this connection string is available.

In Visual Studio: open `AiOralExam.sln`, set `AiOralExam.Api` as the startup project, pick the **http** profile and press F5.

### Try the M1 exam flow in Swagger

1. Click **Authorize** (top right), keep the `openid` scope and click **Authorize** again. The client id `aives-swagger` is already filled in.
2. Sign in on the Keycloak page with `han.hg@aives.edu.vn` / `Password@123`. Swagger then sends the Keycloak access token with every request.
3. Call `GET /api/interview/my-sessions`. You should see 3 sessions: two open (S1, S4) and one not open yet (S6).
4. Call `POST /api/interview-attempts` with `{ "examSessionId": "33333333-0000-0000-0000-000000000001" }`. You get `201`, an `attemptId` and the first question (`currentTurn`).
5. Call `POST /api/interview-attempts/{attemptId}/responses` with `{ "answerText": "..." }`. You get the mock AI score and feedback, plus the next question. Repeat until `outcome = "Completed"`.

> Each question must be answered within `timeLimitPerQuestion` seconds (120 s for S1), plus a 15 s grace period. A later submission counts as timed out (`timedOut: true`) and the answer is not stored.

Sample requests are also in `src/AiOralExam.Api/AiOralExam.Api.http` (VS Code REST Client / Visual Studio).

## 3. Seed data

**Shared password: `Password@123`**. The accounts live in Keycloak (`keycloak/aives-realm.json`); each Keycloak user id equals the `users.id` in the seed. Sign in with the email or the username (the part before `@`, e.g. `han.hg`), which equals `users.username`.

| Email | Role | Use it to test |
|---|---|---|
| `admin@aives.edu.vn`, `tuan.dm@aives.edu.vn` | Administrator | all courses / sessions / reports |
| `an.nv@aives.edu.vn` | Lecturer | SWD392 (S1, S3) |
| `mai.bt@aives.edu.vn` | Lecturer | SWD392 + DBI202 |
| `ha.dt@aives.edu.vn` | Lecturer | DBI202 (S4) |
| `long.nd@aives.edu.vn` | Lecturer | SWT301 (S5) |
| `nam.lq@aives.edu.vn` | Lecturer | PRN232 (S2, S6) |
| `binh.tt@aives.edu.vn` | Lecturer | PRN232 |
| `han.hg@aives.edu.vn` | Student | **not taken yet** – use it to demo the exam flow |
| `chau.lm@aives.edu.vn` | Student | S1 finished, **score confirmed** → can view the report |
| `dung.pq@aives.edu.vn` | Student | S1 finished, **waiting for review** → report metadata only; scores and dialogue are hidden |
| `khoa.vt@aives.edu.vn` | Student | temporary password → Keycloak asks for a new one at first sign-in |
| `duy.hg@aives.edu.vn` | Student | **disabled** in Keycloak → sign-in fails |
| 36 other students (`SE180005`…`SE180040`) | Student | see the `users` table |

| Exam session | Course | Status | Questions | Notes |
|---|---|---|---|---|
| S1 `33333333-0000-0000-0000-000000000001` | SWD392 | Published, **open** | 3 | 9 attempts (finalized / pending review / in progress) |
| S2 `…0002` | PRN232 | Draft | 4 | hidden from students |
| S3 `…0003` | SWD392 | Closed | 4 | 12 attempts – use it for class statistics |
| S4 `…0004` | DBI202 | Published, **open** | 4 | 5 attempts; Han is registered |
| S5 `…0005` | SWT301 | Closed | 4 | 8 attempts |
| S6 `…0006` | PRN232 | Published, **not open yet** (opens in 3 days) | 3 | use it to test the `exam_session_not_open` error |

The opening times of S1/S4/S6 are relative to `now()` when the seed runs, so a fresh database always works for a demo.

**Reset the data:**

```bash
docker compose down -v     # -v also deletes the data volumes (app database AND Keycloak)
docker compose up -d       # runs 01_schema.sql + 02_seed.sql and imports the Keycloak realm again
```

**Browse the database:** pgAdmin at http://localhost:5050 (`admin@aives.edu.vn` / `admin123`). Add a server with host `postgres`, port `5432`, user `aives`, password `aives@123`. DBeaver / DataGrip work too with host `localhost:5432`.

## 4. Project structure

```
ai-oral-exam-api/
├── AiOralExam.sln
├── docker-compose.yml               # PostgreSQL 16 + pgAdmin + Keycloak (+ its database, Mailpit)
├── keycloak/                        # Keycloak rea│   ├── 13_f3_durable_audio_acceptance.sql # durable audio/retry metadata
│   └── 14_f3_manual_turn_recovery.sql # one bounded manual recovery per turn
        # shared settings: net8.0, nullable...
├── Directory.Packages.props         # NuGet versions declared once (Central Package Management)
├── database/
│   ├── 01_schema.sql                # SOURCE OF TRUTH for the schema (16 tables)
│   ├── 02_seed.sql                  # demo data
│   ├── 03_user_role_code_constraint.sql # upgrade for existing databases
│   ├── 04_audit_log_description.sql # adds audit log descriptions to existing databases
│   ├── 05_allow_interview_attempt_retakes.sql # removes the old one-attempt constraint
│   ├── 06_rubric_versions.sql ... 12_f3_interrupted_attempts.sql # forward-only upgrades
│   └── 13_f3_durable_audio_acceptance.sql # durable audio/retry metadata
├── src/
│   ├── AiOralExam.Api/              # Host: Program.cs, Keycloak token validation, Swagger, CORS, error middleware
│   ├── Shared/
│   │   └── AiOralExam.SharedKernel/ # shared errors (AppException, ApiError), Roles, ICurrentUser
│   └── Modules/
│       ├── AccessConfig/            # F7 - Access Control & Exam Configuration
│       │   ├── AiOralExam.Modules.AccessConfig.Contracts/  # public interfaces + DTOs for other modules
│       │   └── AiOralExam.Modules.AccessConfig/            # Domain, Infrastructure (DbContext), Application, Controllers
│       │       └── Features/Users/ # CreateUser, GetUser, ListUsers, UpdateUser, ChangeRole, UpdateOwnProfile slices
│       ├── Interview/               # F3 - AI Viva Interview
│       │   ├── AiOralExam.Modules.Interview.Contracts/
│       │   └── AiOralExam.Modules.Interview/
│       │       ├── Domain/          # Participant, InterviewAttempt (state machine), QuestionResponse, InterviewTurn...
│       │       ├── Application/     # InterviewService (orchestrates the exam flow)
│       │       │   └── Ai/          # IAnswerAnalyzer, IRubricScorer, ISpeechToText, ITextToSpeech, ILlmService
│       │       │       └── Mock/    # mocks for M1
│       │       ├── Infrastructure/  # InterviewDbContext
│       │       └── Controllers/
│       └── Reporting/               # F6 - Feedback & Reporting (no tables, read-only)
└── tests/
    └── AiOralExam.UnitTests/        # xUnit
```

### Modules and the data they own

| Module | Does | Owns tables | Depends on |
|---|---|---|---|
| **AccessConfig** (F7) | user profiles, lecturer assignment, exam sessions, questions, rubrics, AI settings, audit log | `users`, `roles`, `courses`, `course_lecturers`, `exam_sessions`, `questions`, `rubrics`, `rubric_criteria`, `ai_service_configs`, `audit_logs` | – |
| **Interview** (F3) | runs the exam, follow-ups, AI score suggestions (per rubric criterion), lecturer score review and finalization | `participants`, `interview_attempts`, `question_responses`, `interview_turns`, `criterion_scores`, `score_reviews` | `AccessConfig.Contracts` |
| **Reporting** (F6) | student reports, results, class statistics, grade sheet export | *(none)* | `Interview.Contracts`, `AccessConfig.Contracts` |

**Three rules that keep the modules apart:**

1. Each module reads and writes **only its own tables** (each module has its own DbContext that maps only its tables).
2. Other modules may reference only the `*.Contracts` projects, **never** a module project directly. For example, Interview gets questions through `IExamConfigurationApi`.
3. Reporting reads stored scores only and **never calls the AI again**.

### Available APIs

| Method | Route | Role | Tracker task |
|---|---|---|---|
| GET | `/api/auth/me` | any | BE-PLAT-07 |
| PATCH | `/api/auth/me` | any | |
| GET | `/api/users`, `/api/admin/users` | Admin | |
| GET | `/api/users/{id}` | Admin | |
| POST | `/api/users`, `/api/admin/users` | Admin | |
| PUT | `/api/users/{id}` | Admin | |
| POST | `/api/admin/users/{id}/setup-link` | Admin | |
| PATCH | `/api/admin/users/{id}/role` | Admin | |
| POST | `/api/admin/users/{id}/deactivate` | Admin | |
| GET | `/api/admin/courses` | Admin | |
| GET | `/api/ai-services`, `/api/admin/ai-settings` | Admin | |
| POST, PUT | `/api/ai-services`, `/api/ai-services/{id}` | Admin | |
| PUT | `/api/admin/ai-settings` | Admin | |
| GET | `/api/courses/mine` | Lecturer, Admin | |
| GET | `/api/exam-sessions`, `/api/exam-sessions/{id}` | Lecturer, Admin | |
| GET | `/api/interview/my-sessions` | Student | FE-INT-01 |
| POST | `/api/interview-attempts` | Student | BE-PLAT-04 |
| GET | `/api/interview-attempts/{id}` | Student | |
| POST | `/api/interview-attempts/{id}/responses` | Student | BE-AI-05 |
| GET | `/api/interview-attempts/{id}/review` | Lecturer | |
| PUT | `/api/interview-attempts/{id}/score-review` | Lecturer | |
| GET | `/api/reports/attempts/{id}` | Student (own report; metadata until confirmed), Lecturer, Admin | M3 |
| GET | `/api/reports/exam-sessions/{id}/results` | Lecturer, Admin | M3 |
| GET | `/api/reports/exam-sessions/{id}/statistics` | Lecturer, Admin | M3 |
| GET | `/api/reports/exam-sessions/{id}/grade-sheet` | Lecturer, Admin | F6 (UTF-8 CSV) |
| GET | `/health` | – | |

The `GET /api/exam-sessions` list includes `participantCount`, `takenCount` (attempts with `PendingReview` or `Finalized` status), `toReviewCount` (attempts with `PendingReview` status), and `inProgressCount` (attempts with `InProgress` status). `PATCH /api/auth/me` accepts a body containing only `fullName` and returns the same profile shape as the GET endpoint.

Published exam sessions are automatically changed to `Closed` after `EndTime` by a background service that checks every 15 seconds. The end time is exclusive: a session is no longer open at the exact `EndTime`. Automatic closure is written to application logs; it does not create a user-attributed audit-log row because no user performs the background action.

#### Audit logs

`GET /api/audit-logs` (also available at `/api/admin/audit-logs`) is administrator-only. Its response includes a nullable `description` alongside the action, target entity/id, timestamp, and actor details. Actions are `Create`, `Update`, `Delete`, `Publish`, `Close`, `ChangeRole`, `Deactivate`, `Reactivate`, `AssignLecturer`, `RemoveLecturer`, `ImportParticipants`, `RemoveParticipant`, `ResendSetupEmail`, and `UpdateOwnProfile`. `ChangeRole` records the previous and new role; `Deactivate` and `Reactivate` record account activation changes. Entity names are `User`, `Course`, `ExamSession`, `Question`, `Rubric`, and `AiServiceConfig`. The Interview module writes its own audit entries (for example action `ApproveScore`, entity `InterviewAttempt`), which are intentionally not part of the AccessConfig action list.

AI service configuration responses include `hasApiKey`; the API never returns the API key or its ciphertext. When updating an existing configuration, omit `apiKey` or send it as blank/whitespace to keep the stored key only if the provider and endpoint are unchanged (provider comparison ignores case; endpoint comparison ignores case and a trailing slash). A new nonblank `apiKey` is required when changing the provider or endpoint. Creating a configuration (including the first `PUT /api/admin/ai-settings` for a service type) also requires a nonblank `apiKey`; provide a new nonblank value to replace the key.

Every error uses the same format (FOUNDATION-02):

```json
{ "code": "exam_session_not_open", "message": "The exam session is not open.", "traceId": "0HN...", "details": null }
```

The FE should `switch` on `code`; `message` is meant for display.

### Username rule

An admin user's username is the email local part, lowercased. Users with the same local part on different domains (for example, `a@x.com` and `a@y.com`) collide; the second create fails with `409 username_exists`. A final rule is pending a team decision.

### GET `/api/auth/me` without a database user row

A valid Keycloak token alone is not enough; the user must have an active AIVES database row, 
created through the admin API.
In the current HTTP pipeline, a missing or inactive row is rejected during token validation with `403 account_inactive` before `/api/auth/me` reaches its profile lookup.
The profile service itself reports `404 user_not_found` if called for an unknown ID, but that is not the current endpoint response for a missing row.

### Source of truth for roles and active status

The AIVES database is the source of truth for authorization: its role replaces the token role, 
and an inactive or missing user row is denied by the API.
Changing a user's role or deactivating them in AIVES does not currently update the Keycloak account. 
Admin user creation also provisions the Keycloak identity and assigns its initial realm role; 
Keycloak handles authentication and password setup, but subsequent role and active-status changes remain database-only. 
Changing the full name through `PATCH /api/auth/me` updates only the AIVES profile and is not synchronized to Keycloak.

### Attempt states (BE-AI-01)

```
InProgress ──(last question answered)──▶ PendingReview ──(lecturer confirms the score)──▶ Finalized
```

There is no way back. Each submitted answer returns an `outcome`:

- `FollowUp`: the AI asks a follow-up; `currentTurn.type = "FollowUp"`
- `NextQuestion`: moves on to the next main question
- `Completed`: no questions left; the attempt moves to `PendingReview`

### Audio retry and recovery notes (current phase)

- Turn-processing retries are bounded by `Interview:MaximumTurnRetries`; terminal failures move the attempt to `Interrupted`.
- Missing audio on a retry is treated as terminal and does not loop forever.
- Restart recovery depends on a durable `IAudioChunkStore` implementation and a real `ISpeechToTextService` provider.
- The default in-memory adapters are development-only and are not a production durability guarantee.

### F6 reporting and F3 Score Review

Reporting reads existing data through `IInterviewResultsApi` and
`IExamConfigurationApi`. It does not write reviews, finalize attempts, or call AI.
A confirmed final score means a stored `ScoreReview` with status `Approved`.
Lecturers can inspect an attempt through `GET /api/interview-attempts/{id}/review`
and submit its final score through `PUT /api/interview-attempts/{id}/score-review`.
Submission stores the approved review and finalizes the attempt. An AI score is
never substituted for a lecturer-confirmed final score.

- **Student report:** `GET /api/reports/attempts/{id}` returns HTTP 200 for the
  student's own pending attempt, with its status and basic metadata.
  `suggestedTotalScore`, `finalScore`, and `lecturerComment` are null, and
  `questions` is empty. Once confirmed, the report includes stored AI scores,
  feedback, ordered dialogue, and the lecturer's final score/comment.
- **Lecturer/admin results:** `GET /api/reports/exam-sessions/{id}/results`
  lists finished attempts, including pending ones. Lecturers must be assigned
  to the session's course. They can also read pending report details.
- **Statistics:** `GET /api/reports/exam-sessions/{id}/statistics` counts only
  confirmed attempts in averages, question difficulty, good-answer rates, and
  score distribution. Missing/unanswered questions contribute zero. Question
  `answerCount` is the number of confirmed attempts counted, including those
  unanswered questions. Completion/pending counters describe all finished attempts.
  With no confirmed scores, the final-score average is null and distributions
  have zero counts. Final-score distribution is normalized to a 10-point scale.
- **CSV:** `GET /api/reports/exam-sessions/{id}/grade-sheet` downloads UTF-8
  with a BOM for Vietnamese names in Excel. Columns are
  `No,StudentCode,FullName,Status,FinalScore,MaxScore`.
  Confirmed rows have `Finalized` status and the stored final score; other rows
  have `PendingReview` status and a blank final score. Scores use the exam's
  original points scale, indicated by `MaxScore`. AI suggestions are excluded.
  Text cells preserve commas, quotes, and line breaks, and formula-like names
  or codes are escaped as text. Rows cover finished attempts returned by F3's
  read contract, not students who have not completed an attempt.

To demo with the existing seed, use S1 (`33333333-0000-0000-0000-000000000001`):
`chau.lm@aives.edu.vn` can read confirmed attempt
`66666666-0000-0000-0000-000000000001`; `dung.pq@aives.edu.vn` gets pending
metadata for attempt `66666666-0000-0000-0000-000000000002`.
`an.nv@aives.edu.vn` can read S1 results/statistics and download its CSV.
Sign in through Keycloak/Swagger with `Password@123`; the seeded pending and
confirmed examples can be read without submitting a new review.

## 5. Configuration (`appsettings.json`)

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Database` | `Host=localhost;Port=5432;Database=aives;Username=aives;Password=aives@123` | matches `docker-compose.yml` |
| `Keycloak:Authority` | `http://localhost:8080/realms/aives` | Keycloak realm that issues the tokens; its signing keys are read from `{Authority}/.well-known/openid-configuration` |
| `Keycloak:Audience` | `aives-api` | tokens must contain this audience |
| `Keycloak:SwaggerClientId` | `aives-swagger` | client used by Swagger's **Authorize** button |
| `Interview:ShowAiScoreToStudent` | `true` | M1 shows the mock score to the student right away. From M3 on, set `false` (students see scores only after the lecturer confirms them) |
| `Interview:Mock:EnableFollowUps` | `false` | turn on to try the follow-up flow with the mock (answers under 15 words trigger a follow-up) |
| `Interview:AllowRetake` | `false` | globally allows another attempt after a student's prior attempt is finished; not a per-session setting |
| `Interview:TimeLimitGraceSeconds` | 15 | grace period after the time limit |
| `Cors:AllowedOrigins` | `http://localhost:5173`, `http://localhost:3000` | where the FE runs |

Admin user creation provisions the Keycloak identity, assigns its initial realm role, stores the AIVES profile, and requests a password-setup email. The response reports whether the email request succeeded. The username is derived from the email local part (see [Username rule](#username-rule)).

To override settings on your machine without committing them, use `dotnet user-secrets` (the project already has a `UserSecretsId`) or environment variables, e.g. `ConnectionStrings__Database=...`.

Set the Keycloak admin client secret for local development with:

```sh
dotnet user-secrets set "KeycloakAdmin:ClientSecret" "dev-aives-admin-api-secret" --project src/AiOralExam.Api
```

This is a development-only value matching `keycloak/aives-realm.json`. Do not put
the secret in any `appsettings*.json` file.

## 6. Changing the database

The team chose **`database/01_schema.sql` as the source of truth** (no EF migrations). To add or change a column or table:

1. Edit `database/01_schema.sql` (and `02_seed.sql` if the demo data is affected).
   - Run the applicable forward-only upgrade scripts on existing databases; `database/04_audit_log_description.sql` adds the audit-log description column.
2. Update the entity in `Domain/` and the mapping in the `*DbContext.cs` of **the module that owns the table**.
   - PascalCase property names map to snake_case automatically: `TimeLimitPerAnswerSeconds` ↔ `time_limit_per_answer_seconds`.
   - Enums are stored as strings; their names must match the `CHECK` constraints in the SQL.
3. Reset only a disposable local development database when appropriate: `docker compose down -v && docker compose up -d`.

**Never rerun `database/01_schema.sql` against an existing database.** It starts by dropping tables with `CASCADE`; it is for fresh database initialization only. Existing databases must use the applicable forward-only upgrade scripts.

### Existing databases: enable interview retakes

The `database/05_allow_interview_attempt_retakes.sql` upgrade requires an explicit `app_schema`, removes only the old single-column unique constraint on `interview_attempts.participant_id`, and ensures its non-unique index exists. It does not update or delete rows. It fails if the selected schema/table is missing or ambiguous, or if a conflicting standalone unique index exists.

Connect to the application database as the same database user used by the API. Check the resolved application table and candidate schemas:

```sql
SELECT current_database(), current_user, current_schema(), current_schemas(true),
       to_regclass('interview_attempts') AS resolved_interview_attempts;
SHOW search_path;

SELECT n.nspname AS resolved_schema
FROM pg_class t
JOIN pg_namespace n ON n.oid = t.relnamespace
WHERE t.oid = to_regclass('interview_attempts');

SELECT n.nspname AS schema_name
FROM pg_class t
JOIN pg_namespace n ON n.oid = t.relnamespace
WHERE t.relname = 'interview_attempts'
  AND t.relkind IN ('r', 'p')
  AND n.nspname <> 'information_schema'
  AND n.nspname !~ '^pg_';
```

Use `resolved_schema` for the API's active search path. Pass it explicitly as `app_schema`; do not infer or guess when the target is unclear. The upgrade validates only that schema, so databases containing both `fresh` and `public` are supported when the correct one is selected. If the resolved table is null, or the application schema cannot be established, stop and resolve the configuration first. Apply the upgrade (the database URL must target the application database):

```bash
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -v app_schema=public -f database/05_allow_interview_attempt_retakes.sql
```

Replace `public` with the verified schema name. The script requires this psql variable and aborts if the selected schema/table is missing or ambiguous.

The upgrade is transactional. Its regular index build may briefly block writes; schedule it accordingly for a large or busy table.

## 7. Tests

```bash
dotnet build
dotnet test
```

Current unit tests cover the attempt state machine, deterministic mock AI,
confirmed-only statistics, pending-report privacy, report authorization,
stored report details, and CSV encoding/escaping and final-score handling.

## 8. Troubleshooting

| Symptom | Fix |
|---|---|
| `docker compose up` says port 5432 is already in use | Another PostgreSQL is running locally. Change `"5432:5432"` to `"5433:5432"` and use `Port=5433` in the connection string |
| Edited the SQL files but the database did not change | The scripts only run on an empty volume → `docker compose down -v`, then `up -d` |
| `/health` reports Unhealthy | The container is not running or not healthy yet: `docker compose ps`, `docker compose logs postgres` |
| Every request returns `401` / the Keycloak sign-in page does not open | Keycloak is not running: `docker compose ps`, `docker compose logs keycloak` |
| `403 account_inactive` | The user is missing from `users` or has `is_active = false` |
| `401 unauthenticated` in Swagger | You have not clicked **Authorize**, or the token has expired (5 minutes) → click **Authorize** again |
| Changed `keycloak/aives-realm.json` but Keycloak did not change | The realm is imported only once → see [keycloak/README.md](keycloak/README.md) |
| `409 attempt_already_taken` | That student has already taken this session. Use `han.hg` or reset the database |
| `409 exam_session_not_open` | The session is not published or is outside its opening hours (S6 is like this on purpose) |

## 9. Next steps (from the tracker)

- **M2 – Adaptive AI interview:** replace the `Mock*` classes in `Interview/Application/Ai` with real adapters (OpenAI / Gemini / Google TTS…) and switch the registrations in `InterviewModule.cs`; add an audio upload API + WebSocket.

Google sign-in is not configured by default and requires OAuth credentials. Keycloak handles authentication and password setup/reset flows; see [keycloak/README.md](keycloak/README.md).

> Note: Microsoft supports .NET 8 LTS until **10 November 2026**. The project keeps running after that date; to move to .NET 10 LTS, change `TargetFramework` in `Directory.Build.props` and the package versions in `Directory.Packages.props`.
