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
├── keycloak/                        # Keycloak realm (clients, roles, demo users) + its README
├── Directory.Build.props            # shared settings: net8.0, nullable...
├── Directory.Packages.props         # NuGet versions declared once (Central Package Management)
├── database/
│   ├── 01_schema.sql                # SOURCE OF TRUTH for the schema (16 tables)
│   └── 02_seed.sql                  # demo data
├── src/
│   ├── AiOralExam.Api/              # Host: Program.cs, Keycloak token validation, Swagger, CORS, error middleware
│   ├── Shared/
│   │   └── AiOralExam.SharedKernel/ # shared errors (AppException, ApiError), Roles, ICurrentUser
│   └── Modules/
│       ├── AccessConfig/            # F7 - Access Control & Exam Configuration
│       │   ├── AiOralExam.Modules.AccessConfig.Contracts/  # public interfaces + DTOs for other modules
│       │   └── AiOralExam.Modules.AccessConfig/            # Domain, Infrastructure (DbContext), Application, Controllers
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
| **Interview** (F3) | runs the exam, follow-ups, AI score suggestions (per rubric criterion), (M3) lecturer review | `participants`, `interview_attempts`, `question_responses`, `interview_turns`, `criterion_scores`, `score_reviews` | `AccessConfig.Contracts` |
| **Reporting** (F6) | student reports, results, class statistics, grade sheet export | *(none)* | `Interview.Contracts`, `AccessConfig.Contracts` |

**Three rules that keep the modules apart:**

1. Each module reads and writes **only its own tables** (each module has its own DbContext that maps only its tables).
2. Other modules may reference only the `*.Contracts` projects, **never** a module project directly. For example, Interview gets questions through `IExamConfigurationApi`.
3. Reporting reads stored scores only and **never calls the AI again**.

### Available APIs

| Method | Route | Role | Tracker task |
|---|---|---|---|
| GET | `/api/auth/me` | any | BE-PLAT-07 |
| GET | `/api/courses/mine` | Lecturer, Admin | |
| GET | `/api/exam-sessions`, `/api/exam-sessions/{id}` | Lecturer, Admin | |
| GET | `/api/interview/my-sessions` | Student | FE-INT-01 |
| POST | `/api/interview-attempts` | Student | BE-PLAT-04 |
| GET | `/api/interview-attempts/{id}` | Student | |
| POST | `/api/interview-attempts/{id}/responses` | Student | BE-AI-05 |
| GET | `/api/reports/attempts/{id}` | Student (own report; metadata until confirmed), Lecturer, Admin | M3 |
| GET | `/api/reports/exam-sessions/{id}/results` | Lecturer, Admin | M3 |
| GET | `/api/reports/exam-sessions/{id}/statistics` | Lecturer, Admin | M3 |
| GET | `/api/reports/exam-sessions/{id}/grade-sheet` | Lecturer, Admin | F6 (UTF-8 CSV) |
| GET | `/health` | – | |

Every error uses the same format (FOUNDATION-02):

```json
{ "code": "exam_session_not_open", "message": "The exam session is not open.", "traceId": "0HN...", "details": null }
```

The FE should `switch` on `code`; `message` is meant for display.

### Attempt states (BE-AI-01)

```
InProgress ──(last question answered)──▶ PendingReview ──(lecturer confirms the score)──▶ Finalized
```

There is no way back. Each submitted answer returns an `outcome`:

- `FollowUp`: the AI asks a follow-up; `currentTurn.type = "FollowUp"`
- `NextQuestion`: moves on to the next main question
- `Completed`: no questions left; the attempt moves to `PendingReview`

### F6 reporting without the F3 score-approval API

Reporting reads existing data through `IInterviewResultsApi` and
`IExamConfigurationApi`. It does not write reviews, finalize attempts, or call AI.
A confirmed final score means a stored `ScoreReview` with status `Approved`.
The seed includes these records, so all F6 endpoints can be tested before the
lecturer approval API is implemented. New attempts remain pending until F3
provides that workflow; an AI score is never substituted for a final score.

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
Sign in through Keycloak/Swagger with `Password@123`; no manual database updates
or score-approval endpoint are needed for these seeded examples.

## 5. Configuration (`appsettings.json`)

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Database` | `Host=localhost;Port=5432;Database=aives;Username=aives;Password=aives@123` | matches `docker-compose.yml` |
| `Keycloak:Authority` | `http://localhost:8080/realms/aives` | Keycloak realm that issues the tokens; its signing keys are read from `{Authority}/.well-known/openid-configuration` |
| `Keycloak:Audience` | `aives-api` | tokens must contain this audience |
| `Keycloak:SwaggerClientId` | `aives-swagger` | client used by Swagger's **Authorize** button |
| `Interview:ShowAiScoreToStudent` | `true` | M1 shows the mock score to the student right away. From M3 on, set `false` (students see scores only after the lecturer confirms them) |
| `Interview:Mock:EnableFollowUps` | `false` | turn on to try the follow-up flow with the mock (answers under 15 words trigger a follow-up) |
| `Interview:AllowRetake` | `false` | whether students may retake an exam (open point) |
| `Interview:TimeLimitGraceSeconds` | 15 | grace period after the time limit |
| `Cors:AllowedOrigins` | `http://localhost:5173`, `http://localhost:3000` | where the FE runs |

To override settings on your machine without committing them, use `dotnet user-secrets` (the project already has a `UserSecretsId`) or environment variables, e.g. `ConnectionStrings__Database=...`.

## 6. Changing the database

The team chose **`database/01_schema.sql` as the source of truth** (no EF migrations). To add or change a column or table:

1. Edit `database/01_schema.sql` (and `02_seed.sql` if the demo data is affected).
2. Update the entity in `Domain/` and the mapping in the `*DbContext.cs` of **the module that owns the table**.
   - PascalCase property names map to snake_case automatically: `TimeLimitPerAnswerSeconds` ↔ `time_limit_per_answer_seconds`.
   - Enums are stored as strings; their names must match the `CHECK` constraints in the SQL.
3. Reset the database: `docker compose down -v && docker compose up -d`.
4. Tell the team to reset their databases after the merge.

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
- **M3 – Score review:** lecturer API to review transcripts and confirm scores (`ScoreReview` → attempt `Finalized`). F6 reads those stored reviews; its CSV export and reporting are available independently (see section 4).
- **M4 – Access & exam configuration:** CRUD for sessions / questions / rubrics, publishing (locks the questions), account management (creates the user in Keycloak too, through its Admin API), AI settings, audit logging. Google SSO and password reset are configured in Keycloak, not in the API.

The places to work on are marked `TODO (M2/M3/M4)` in the code.

> Note: Microsoft supports .NET 8 LTS until **10 November 2026**. The project keeps running after that date; to move to .NET 10 LTS, change `TargetFramework` in `Directory.Build.props` and the package versions in `Directory.Packages.props`.
