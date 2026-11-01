Requesting changes.

I tested this PR head in disposable Docker and isolated runners. The PostgreSQL happy path for import, approval, and attach passed, but a synchronized import probe persisted two identical bank items, and an existing-content duplicate returned a generic conflict instead of row-specific validation. Ten service tests passed in an isolated runner after supplying a temporary namespace shim; the repository test file itself is missing that namespace import. The full solution test run is blocked by compile errors in the merged interview sources.

The review also includes the Ponytail finding that list requests load and return the full revision history for every item. CI currently has no PostgreSQL/Keycloak integration or E2E job, as required by TASK-K. Please address the F1 findings and coordinate the shared interview build blockers and E2E coverage with their owners.

/src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/QuestionBank/QuestionBankContracts.cs lines 73-76
[P1] Enforce duplicate uniqueness in the database. This read and the later insert are separate operations, and the schema has no unique key for course plus normalized content. I reproduced two synchronized PostgreSQL imports both passing this check and persisting duplicate items. Please add a database-enforced uniqueness rule and map its violation to a conflict so concurrent requests cannot bypass this check.

src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/QuestionBank/QuestionBankContracts.cs lines 76-79
[P2] Return row-specific errors for existing duplicates. Duplicates within the submitted batch report items[i].content, but a match against existing bank content returns one generic conflict here. The import UI cannot identify the offending row, which misses the F1-B row-error contract. Collect matching row indices and return field-path validation details.

src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/QuestionBank/QuestionBankContracts.cs lines 18-21
[P2] Keep revision history out of list responses. This eagerly loads every revision for every item, and ToDto includes revision content and provenance. As history grows, each list refresh loads and sends the full history; the detail route can fetch it on demand. Use a compact list projection and load revisions in GetAsync.

tests/AiOralExam.UnitTests/ReportCalculatorTests.cs lines 275-278
[P1] Import the namespace for ExamSessionStatus. The enum is declared in AiOralExam.Modules.AccessConfig.Contracts, but this file imports only the Domain namespace. The isolated build failed with CS0103 here; add the missing using so the test project compiles.

.github/workflows/backend-ci.yml lines 24-27
[P2] Add the planned PostgreSQL/Keycloak integration job. This workflow only runs dotnet test, while the HTTP test factory substitutes EF InMemory and fake Keycloak. TASK-K calls for real PostgreSQL/Keycloak integration or E2E coverage, so the current CI cannot verify that contract.

Backend CI failure report:

This job failed because the test run hit a runtime exception in EF Core/LINQ rather than a normal application/business-logic failure.

The key evidence from the logs is this recurring error:

- System.InvalidOperationException: An exception was thrown while attempting to evaluate a LINQ query parameter expression
- Inner error: GenericArguments[1], 'System.ReadOnlySpan`1[System.Guid]' / 'System.ReadOnlySpan`1[System.Int32]' violates the constraint of type 'TRet'
This indicates a query is building a LINQ expression that includes a ReadOnlySpan value, and EF Core is trying to parameterize/evaluate it and crashes while creating a generic expression. That is why multiple tests fail with the same low-level exception instead of the expected domain exceptions.

The failing tests show the impact clearly:

- Unit tests expected ConflictException or ValidationException, but actual exception was InvalidOperationException
- Integration tests returned InternalServerError / wrong HTTP status because the service failed before reaching the intended validation or conflict handling
Summary of the failing counts from the job log:

- Unit tests: 5 failed, 315 passed, 3 skipped
- Integration tests: 3 failed, 41 passed
- Overall: failed with exit code 1
So the root cause is not “one broken endpoint” but a shared LINQ/EF query issue triggered by ReadOnlySpan-based values in the query pipeline, which breaks several flows and causes the suite to fail.

## Source-checked resolution plan

The steps below are based on the current checkout. Keep the report above as evidence of the tested PR head; where the checkout differs from the report, verify against the exact branch and CI logs before making an additional change.

### 1. Make question-bank content unique under concurrent writes (P1)

**Current state:** The checkout has a named unique index on `(course_id, content_hash)` in `database/01_schema.sql` and the EF mapping, and maps that index's PostgreSQL violation to `question_bank_duplicate`. Migration `13_f1_question_bank_content_unique.sql` additionally installs a generated `lower(btrim(content))` column and a second unique index; these two normalization rules are not identical to the SQL hash expression in `QuestionBankContentHash`. The CI concurrency test exercises the hash query and is opt-in outside CI.

**Changes and ownership:**

1. Reconcile the current hash normalizer (`upper(btrim(regexp_replace(content, '\\s+', ' ', 'g')))`) with migration `13`'s `lower(btrim(content))` key. Do not assume PostgreSQL case folding and .NET ordinal case-insensitive comparison are identical for every Unicode string.
2. Coordinate with the database owner (TASK-K does not own `database/`) whether the current unique hash index is the canonical guard or whether the generated normalized key should be promoted into `database/01_schema.sql`. Avoid retaining two disagreeing uniqueness definitions.
3. Before applying the upgrade, detect and report existing collisions using the agreed normalization. Resolve them explicitly with the data owner; do not silently delete or rewrite existing bank items. The migration must fail clearly if collisions remain.
4. The service now translates only `23505` for the named content-hash or normalized-content indexes into `question_bank_duplicate` across create, import, AI draft creation, and Draft update. Keep unrelated database failures unmodified.
5. Keep the precheck for a friendly response, but treat the database constraint as the final concurrency guard. A concurrent loser must return the documented conflict response, and an atomic import must not leave a partial batch.

**Verification:** PostgreSQL tests cover synchronized duplicate imports, conflict mapping, and the attach ID filter. They are configured for the CI disposable database but were skipped in this local run, so a green PostgreSQL CI job is still required to prove race behavior and migration compatibility.

### 2. Report existing-content duplicates by import row (P2)

**Current state:** both in-batch and pre-existing duplicates now produce `items[i].content` validation details. Unit and HTTP tests cover one and multiple pre-existing matches and verify that no import rows/audits are written.

**Changes:**

1. Keep the existing row-indexed `ValidationException` checks before creating entities, audit rows, or revisions. A database race remains a conflict because it cannot reliably be converted into row-specific validation after the race.
2. The unit/HTTP contract tests assert standard validation status/code, field paths, normalization, and atomic no-write behavior.

### 3. Keep revision history out of bank list responses (P2)

**Current state:** `ListAsync` projects to `QuestionBankItemListDto` without loading revisions. Detail still loads and returns revision history; the HTTP contract test verifies both shapes.

**Changes:**

1. Preserve the compact projection, course authorization, status filtering, ordering, and list semantics.
2. Keep the detail response and `GetAsync` revision loading intact; list JSON omits revisions while detail JSON includes them.

### 4. ReportCalculator test namespace finding (P1)

The current `tests/AiOralExam.UnitTests/ReportCalculatorTests.cs` already has `using AiOralExam.Modules.AccessConfig.Contracts;`, which is the namespace identified by the report. Therefore, the requested missing-using change appears already present in this checkout; do not add a duplicate import. Build the unit-test project on the exact branch under review and confirm `ExamSessionStatus` resolves. If CI still reports CS0103, compare its source revision and compiler output with this checkout and restore the import only on the branch where it is actually absent.

### 5. Add real PostgreSQL/Keycloak verification to CI (P2 / TASK-K)

**Current state:** `.github/workflows/backend-ci.yml` has a separate PostgreSQL/Keycloak contract job that initializes a disposable schema, verifies migration behavior, runs opt-in PostgreSQL tests, and checks a Keycloak-issued service-account token against `/api/auth/me`. Ordinary `ApiTestFactory` HTTP contract tests still use EF InMemory and a fake Keycloak client. The job does not exercise the full TASK-K publish/interview/review/feedback/report flow.

**Changes and ownership:**

1. Extend the existing isolated PostgreSQL/Keycloak job with role-specific test identities and a real-token E2E harness for publish, interview, lecturer review, student feedback, and reporting/statistics/CSV.
2. Coordinate API fixture and F3 interview/review details with the frontend and F3 owners; add voice only for paths supported by the configured test providers, plus permission-denied and relevant retry/timeout cases.
3. Use an authorization-code/PKCE-capable browser or test client rather than enabling password grants on the public clients. Keep generated credentials ephemeral and preserve the fast in-memory suite.

**Verification:** run the workflow from a clean database/realm state and demonstrate that it fails when PostgreSQL or Keycloak is unavailable or when the tested auth/schema contract is broken. Existing in-memory tests should continue to run independently.

### 6. Diagnose and fix the EF Core/LINQ `ReadOnlySpan` runtime exception

The failed run's review comment identifies the failing attach filter in `QuestionBankService`. It now uses explicit `Enumerable.Contains` on the materialized `Guid[]`, avoiding the span-backed overload that EF parameter evaluation selected. The successful InMemory HTTP attach contract passes, and an opt-in PostgreSQL regression test now covers the same filter against Npgsql.

1. The failed-run finding is fixed narrowly at the attach query; unrelated collection filters were not changed.
2. The in-memory HTTP contract passes. The Npgsql regression is opt-in and awaits execution in the disposable PostgreSQL CI job.

### 7. Coordinate the merged Interview build blockers with their owners

The exact CI output identified the duplicate `TurnId` parameter and duplicate `InterviewOptions.Language` property; both plus the duplicate `Audio` using are fixed in this checkout. The full solution build and tests now pass locally. If TASK-K ownership requires F3 owner review, share these compiler fixes with Minh/Phuc and Khang before merge.

### Completion order and acceptance checks

1. Run the existing PostgreSQL/Keycloak CI job from a clean state to verify the migration reset, format fix, import hash binding, Npgsql attach filter, and concurrent duplicate guard.
2. Reconcile the service hash normalization and migration generated-column normalization with the database owner before changing `database/01_schema.sql`.
3. Complete the TASK-K role-authenticated E2E flow with frontend/F3 owner coordination; record the exact command and results in CI.
4. Local validation completed with `dotnet test AiOralExam.sln --configuration Release --no-restore`: 366 unit and 52 integration tests passed; 11 unit and 13 integration tests were skipped, including all opt-in PostgreSQL cases. Acceptance still requires the PostgreSQL CI job and end-to-end coverage to pass.

## Implementation status (2026-11-01)

- Fixed the reported Interview compilation errors: the duplicate `TurnId` parameter, duplicate `InterviewOptions.Language` property, and duplicate `Audio` using. The remaining `CurrentTurnDto` constructor now matches the response/turn IDs supplied by `InterviewService`.
- Fixed the question-bank attach query by explicitly using `Enumerable.Contains`; the successful HTTP attach contract now passes. Existing and in-batch import duplicates return row-specific `items[i].content` validation errors, and the list route now projects a compact DTO without revisions while detail still returns revision history.
- Fixed the migration's nested PostgreSQL `format()` placeholders and made the CI migration reset tolerate a fresh schema without the generated column/index. Its duplicate fixture now supplies distinct `content_hash` values so the migration itself can detect normalized collisions.
- `QuestionBankContentHash.ComputeManyAsync` in this checkout does not use `SqlQueryRaw`; it binds the `string[]` as one typed Npgsql `text[]` parameter. No code change was needed for that older review observation.
- `dotnet test AiOralExam.sln --configuration Release --no-restore` passes locally: 366 unit tests and 52 integration tests passed; 11 unit and 13 integration tests were skipped. The PostgreSQL-only concurrency/recovery tests were skipped because no isolated PostgreSQL test connection was configured, so the migration and race behavior still require the CI PostgreSQL job to run.
- **Still outstanding: TASK-K end-to-end coverage.** The current PostgreSQL/Keycloak job checks a Keycloak-issued token at `/api/auth/me`; the concurrency tests call services directly. Neither exercises publish → student interview → lecturer review → student feedback → reporting/statistics/CSV. Completing this needs a disposable PostgreSQL schema plus realm test identities, a supported real-token flow for student and lecturer roles, and aligned API/test fixtures for the F3 interview/review and reporting steps. Coordinate the E2E harness with the frontend and F3 owners; use an authorization-code/PKCE-capable browser or test client rather than enabling password grants on the public clients. Keep the existing fast in-memory suite and make each E2E contract stage independently diagnosable in CI.
- **Follow-up CI run 124903080331:** the prior local green run did not cover three EF queries that capture arrays and call the ambiguous `.Contains` extension. EF evaluated the selected `MemoryExtensions.Contains(ReadOnlySpan<T>, T)` expression and failed while compiling its parameter evaluator. `QuestionService.ImportAsync` now explicitly uses `Enumerable.Contains(orderNumbers, ...)`, and `ExamSessionQueries.GetSessionsAsync` explicitly uses `Enumerable.Contains(sessionIds, ...)`; these were the paths behind the three unit failures and two 500 workflow failures. The same array-based attach query received this treatment in the earlier fix.
- Reproduced and verified the follow-up regressions locally: the targeted unit filter passed (7/7), the two named integration workflows passed (2/2), and `dotnet test AiOralExam.sln --configuration Release --no-restore` passed (366 unit, 52 integration; 11 and 13 skipped, respectively). This environment's skipped PostgreSQL tests still need the configured CI database to execute.
- **Pasted workflow log follow-up:** the `SharedVersionWorkflowHttpTests` test itself queried criterion scores with a captured `Guid[] .Contains`, another ambiguous EF expression at `SharedVersionWorkflowHttpTests.cs:210`. It now calls `Enumerable.Contains` explicitly. The full shared-workflow class passes locally (2/2); the earlier application fixes are already present in commit `e5e4470`.
