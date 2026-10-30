# Round 3 Phase 2 Handoff

> Checkpoint for a fresh Agent session before STEP 4. This document distinguishes verified repository facts from proposed next steps. It does not authorize database changes.

## 1. Current progress

### Round 3 Phase 1

- **VERIFIED:** Phase 1 work is present in the working tree. The repository has not been reset, committed, or pushed.
- **VERIFIED:** The current worktree also contains Phase 2 recovery implementation and test artifacts listed below.
- **VERIFIED:** Focused Phase 1/2 unit coverage and the recovery workflow regression suite have passed in recorded runs. The latest full unit-suite evidence is 324 passed, 0 failed, and 3 skipped. No migration has been applied by this agent.
- **VERIFIED:** The latest focused PostgreSQL recovery integration suite ran against the isolated `r3_phase2_test` database with `Search Path=r3_phase2_app,pg_catalog` and no `public` schema; 6 tests passed, with 0 failures and 0 skips. Historical PostgreSQL failures are distinct from this successful isolated run.

### Phase 2

- **VERIFIED — STEP 1 complete:** Recovery domain state and persistence support exist for processing stages/status, retry counters, manual recovery count, processing token, and submission result persistence.
- **VERIFIED — STEP 2 complete:** Recovery processing and fencing are implemented in `InterviewService`, including stale processing handling and PostgreSQL attempt-row locking.
- **VERIFIED — STEP 3 complete:** The synchronous manual recovery application method, HTTP request/response contract, controller route, authorization, and HTTP contract test were added.
- **VERIFIED — STEP 4:** Audit writing uses the existing AccessConfig audit model/service area through `IAuditLogWriter`; successful and failed recovery audit paths are implemented.
- **VERIFIED — STEP 5:** The PostgreSQL recovery test fixture and isolated non-public schema requirements are documented and were exercised against `r3_phase2_test`/`r3_phase2_app` in the latest focused run.
- **VERIFIED — STEP 6:** Post-closure text-answer recovery now uses stage-specific prerequisites and permits AI-evaluation recovery when `AnsweredAt` and `AnswerTranscript` are persisted.
- **VERIFIED — STEP 8:** Successful-recovery audit append failures are logged with non-sensitive identifiers and rethrown with the original exception.
- **IMPLEMENTED and VERIFIED — STEP 9/15:** A focused PostgreSQL test verifies successful recovery audit persistence through the administrator audit read path, including the recovered TurnId in the persisted audit description. The concurrent worker test verifies target-scoped analyzer/scorer invocation counts and the absence of duplicate persisted criterion scores; it does not rely on the global worker sweep count.

## 2. Exact Phase 2 files

### Modified in the current worktree and relevant to Phase 2

- `src/Modules/Interview/AiOralExam.Modules.Interview/Application/Audio/InterviewAudioSubmissionService.cs`
- `src/Modules/Interview/AiOralExam.Modules.Interview/Application/IInterviewService.cs`
- `src/Modules/Interview/AiOralExam.Modules.Interview/Application/InterviewService.cs`
- `src/Modules/Interview/AiOralExam.Modules.Interview/Domain/InterviewAttempt.cs`
- `src/Modules/Interview/AiOralExam.Modules.Interview/Domain/InterviewTurn.cs`
- `src/Modules/Interview/AiOralExam.Modules.Interview/Infrastructure/InterviewDbContext.cs`
- `tests/AiOralExam.IntegrationTests/AiOralExam.IntegrationTests.csproj`
- `tests/AiOralExam.IntegrationTests/Infrastructure/ApiTestFactory.cs`
- `tests/AiOralExam.UnitTests/InterviewAttemptTests.cs`
- `tests/AiOralExam.UnitTests/InterviewTurnWorkflowTests.cs`

### Newly created and relevant to Phase 2

- `database/13_f3_durable_audio_acceptance.sql`
- `database/14_f3_manual_turn_recovery.sql`
- `src/Modules/Interview/AiOralExam.Modules.Interview/Application/InterviewDtos.cs`
- `src/Modules/Interview/AiOralExam.Modules.Interview/Application/RecoverInterruptedTurnContracts.cs`
- `src/Modules/Interview/AiOralExam.Modules.Interview/Controllers/InterviewAttemptRecoveryController.cs`
- `tests/AiOralExam.IntegrationTests/Http/InterviewRecoveryHttpContractTests.cs`
- `tests/AiOralExam.IntegrationTests/PostgresInterviewRecoveryTests.cs`

`README.md` and `database/01_schema.sql` are also modified in the worktree, but their exact Phase 1/Phase 2 ownership was not re-audited for this handoff. Preserve them and do not revert them.

## 3. Important implementation contracts

- **`RecoverInterruptedTurnAsync(...)` — VERIFIED:** `IInterviewService` declares `Task<SubmitAnswerResponse> RecoverInterruptedTurnAsync(Guid attemptId, Guid turnId, CancellationToken ct = default)`. `InterviewService` requires a recovery actor, locks the attempt, verifies the attempt is `Interrupted`, validates the selected turn and recovery stage, calls `PrepareManualRecovery`, creates a new processing token, and executes the claimed turn synchronously.
- **`PrepareManualRecovery(...)` — VERIFIED:** `InterviewTurn.PrepareManualRecovery(AttemptStatus)` requires `Interrupted`, requires `Failed`, rejects a prior manual recovery, resets the stage-specific retry counter, increments the manual counter, and clears retry/lease state.
- **Interrupted -> PendingReview — VERIFIED:** Recovered processing uses the existing completion path. For a recovered final turn, `CompleteRecovered` completes the attempt to `PendingReview`; it does not open another question.
- **`ManualRecoveryCount <= 1` — VERIFIED:** The domain guard rejects count `>= 1`; migration 14 adds a database check for values between 0 and 1.
- **PostgreSQL attempt-row locking — VERIFIED:** `WithAttemptLockAsync` starts a transaction for the Npgsql provider and executes `SELECT id FROM interview_attempts WHERE id = ... FOR UPDATE` before the operation and `SaveChangesAsync`/commit.
- **ProcessingToken fencing — VERIFIED:** Claims receive a new token. Before final success/error state changes, `EnsureProcessingLeaseAsync` reads the persisted token and rejects a replaced lease. A stale/reclaimed worker therefore cannot finalize the newer claim.
- **Recovery stage selection — VERIFIED:** `InterviewTurn.ProcessingStage` selects timeout evaluation, transcription, AI evaluation, audio validation, or awaiting answer from persisted turn state. Manual recovery rejects awaiting-answer and timeout stages and enforces accepted audio/transcript prerequisites for the applicable stages.
- **Controller endpoint and authorization — VERIFIED:** `POST /api/interview-attempts/{id}/recovery`, body `RecoverInterruptedTurnRequest`, is protected by `[Authorize(Roles = Roles.LecturerOrAdmin)]`. The controller validates a non-empty `TurnId` and delegates to the application service.
- **Current synchronous execution behavior — VERIFIED:** The controller request invokes `RecoverInterruptedTurnAsync`, which claims the turn and immediately calls `ProcessClaimedTurnAsync`; no background queue/worker is introduced by this Phase 2 implementation.

## 4. Known limitations

- **VERIFIED:** Audit logging for manual recovery is implemented, but Interview state persistence and AccessConfig audit persistence use separate DbContexts and are not atomic.
- **VERIFIED:** If a successful audit append fails, recovery may already be durably `PendingReview`/`Completed`; the audit exception is logged and rethrown, so the API may report an error despite committed recovery state. A repeated recovery request is not an audit-delivery retry mechanism.
- **VERIFIED:** The latest focused PostgreSQL recovery suite executed against the isolated `r3_phase2_test` database with the non-public `r3_phase2_app,pg_catalog` search path. This evidence does not claim that migrations were applied by the test setup or to a development or production database.
- **HISTORICAL:** Earlier PostgreSQL recovery tests were blocked by the missing `manual_recovery_count` column in the then-current test database. This is not evidence against the latest successful isolated run.
- **VERIFIED:** Migration 14 rejects the `public` schema; it requires an explicitly supplied non-public application schema.
- **VERIFIED:** External STT/AI execution is not guaranteed exactly once. Token fencing protects persisted state, not provider-side duplicate execution.
- **VERIFIED:** `InMemoryAudioChunkStore` is not restart durable.
- **OUT OF SCOPE:** A durable audit outbox/retry mechanism and distributed transaction are not implemented for Phase 1/2.

## 5. STEP 4 implementation and consistency record

### Existing AccessConfig audit contracts and implementation files

**VERIFIED files to inspect first:**

- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/AuditLogs/IAuditLogService.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/AuditLogs/AuditLogService.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/AuditLogs/AuditActions.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/AuditLogs/AuditEntities.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/AuditLogs/AuditLogDto.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Domain/AuditLogs/AuditLog.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Infrastructure/AccessConfigDbContext.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/AccessConfigModule.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Controllers/AuditLogsController.cs`

**VERIFIED:** `IAuditLogService` remains a read/list contract; the separate public `IAuditLogWriter` contract is used for audit append operations.

### Implemented cross-module contract

- **VERIFIED:** `IAuditLogWriter` contains only the actor, action, target entity, target ID, description, and asynchronous append operation required for one audit record.
- **VERIFIED:** `AuditLogService` implements the writer and is registered in `AccessConfigModule`; Interview does not reference AccessConfig’s EF `DbContext` or internal audit entity.
- **VERIFIED:** `InterviewService` writes success and failure recovery audit events with attempt/turn identifiers in the target/description fields.

### Transaction-consistency requirements

- **VERIFIED:** No atomicity is claimed across the separate Interview and AccessConfig DbContexts.
- **VERIFIED:** Audit writing occurs only after recovery eligibility, claim ownership, and durable recovery completion are established.
- **VERIFIED:** Processing failures write failure audits after the failure checkpoint when possible; audit failures are logged without replacing the original processing failure.
- **VERIFIED:** Successful audit failures are logged and rethrown. The current behavior preserves the API failure contract but can produce an error response after recovery has committed.
- **VERIFIED:** A durable outbox/retry mechanism and distributed transaction are outside the current Phase 1/2 scope.

### Required STEP 4 tests

- **VERIFIED:** The focused fault-injection unit test verifies committed recovery state, original audit exception propagation, logging, and no repeated AI/STT execution.
- **VERIFIED:** The PostgreSQL integration test verifies one successful audit record through the administrator audit read path against the explicitly isolated test database, including the recovered TurnId inside the persisted audit description.
- **VERIFIED:** The PostgreSQL concurrency test uses target-scoped analyzer/scorer invocation counters and target-response criterion-score persistence assertions; it does not rely on unrelated rows or the global worker count.

### Only files STEP 4 should inspect or modify

- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig.Contracts/` (inspect existing public-contract conventions; add only the minimal new contract if required)
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/AuditLogs/IAuditLogService.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/AuditLogs/AuditLogService.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/AuditLogs/AuditActions.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/AuditLogs/AuditEntities.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/AuditLogs/AuditLogDto.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Domain/AuditLogs/AuditLog.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Infrastructure/AccessConfigDbContext.cs`
- `src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/AccessConfigModule.cs`
- `src/Modules/Interview/AiOralExam.Modules.Interview/Application/InterviewService.cs`
- Relevant existing unit/integration test files for AccessConfig audit and Interview recovery only

Do not modify SQL migrations or `database/01_schema.sql` for STEP 4 unless a separately approved schema requirement is discovered.

## 6. STEP 5/9 verification checklist and safe PostgreSQL options

### Checklist

1. Confirm the build succeeds.
2. Run focused unit tests for `InterviewAttempt`, `InterviewTurn`, and recovery workflows.
3. Run the HTTP recovery contract tests, including authorization and validation behavior.
4. Verify the isolated PostgreSQL database uses `r3_phase2_test` and the non-public `r3_phase2_app` schema.
5. Have the database owner manually prepare the baseline and apply migration 13 followed by migration 14 only where required, with `app_schema` set to `r3_phase2_app`.
6. Confirm `manual_recovery_count` exists and its constraint is valid before running PostgreSQL recovery tests.
7. Run the PostgreSQL recovery and successful-audit integration tests.
8. Record pass/fail output and any environment-specific skips. Do not use the development database.

### Safe PostgreSQL test-database upgrade options

- **Option A — preferred:** Create/use a disposable isolated test database and a dedicated non-public application schema; the database owner reviews and manually runs migration 13 followed by migration 14 with `app_schema` set to that schema.
- **Option B:** Restore an existing approved isolated test dump into a new database/schema, verify the schema name, then manually apply the two scripts there. Preserve the original dump.
- **Option C:** Provision a fresh isolated test database from the approved schema baseline, then manually apply the scripts in order. Never point tests at `aives`.
- Do not use `public` as the application schema for migration 14, do not run migrations automatically, and do not drop/truncate/recreate a shared database.

## 7. Current test results and evidence boundaries

- **VERIFIED from `step3-http-test-output.txt`:** The HTTP recovery test was attempted and failed because ASP.NET Core rejected validation metadata on the positional record request model, producing HTTP 500 instead of the expected NotFound. This is a recorded prior result, not a claim that the current tree still has exactly that failure.
- **VERIFIED from `postgres-step2-test-output.txt`:** A PostgreSQL HTTP test was attempted and failed with `42703: column i0.manual_recovery_count does not exist`.
- **VERIFIED from `round3-test-log.txt`:** PostgreSQL recovery tests were attempted and blocked by missing credentials (`No password has been provided but the backend requires one`).
- **RECORDED PASS:** Full unit suite passed with 324 passed tests, 0 failed tests, and 3 skipped tests.
- **RECORDED PASS:** The focused PostgreSQL recovery integration suite passed 6 tests with 0 failures and 0 skips against `r3_phase2_test` using `r3_phase2_app,pg_catalog` without `public`.
- **RECORDED PASS:** The successful-audit PostgreSQL test independently asserts the recovered TurnId in the persisted audit description.
- **RECORDED PASS:** The PostgreSQL concurrency test uses target-scoped analyzer/scorer counters and persisted criterion-score assertions to verify one target processing execution without relying on global sweep counts.
- **EVIDENCE BOUNDARY:** The recorded PostgreSQL result does not claim that migrations were applied by test setup or to any development or production database.

## 8. Repository safety rules

- Never access or modify the development database `aives`.
- Never run migrations automatically.
- Never delete existing dumps, patches, or review artifacts.
- Never commit or push without explicit approval.
- Database scripts are source-only until the user applies them manually to an isolated test database.
- Database preparation and migration execution remain user-controlled; no migration is run automatically by the test setup.
