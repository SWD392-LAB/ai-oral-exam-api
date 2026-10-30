using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Audio;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;

namespace AiOralExam.IntegrationTests;

[Collection(nameof(PostgresInterviewRecoveryTestsCollection))]
public sealed class PostgresInterviewRecoveryTests
{
    private const string ConnectionStringVariable = "AIVES_POSTGRES_TEST_CONNECTION_STRING";
    private static readonly SemaphoreSlim RoleSeedLock = new(1, 1);

    [Fact]
    public async Task Stale_audio_validation_is_recovered_to_retry_without_losing_the_turn()
    {
        await using var scenario = await CreateScenarioAsync();
        await scenario.MakeStaleAudioTurnAsync();

        Assert.Equal(0, await scenario.RunCurrentAsync());
        var recovered = await scenario.ReadTurnAsync();
        Assert.Equal(TurnProcessingStatus.Retry, recovered.ProcessingStatus);
        Assert.Null(recovered.ProcessingToken);
        Assert.Equal(1, recovered.RetryCount);
    }

    [Fact]
    public async Task Concurrent_recovery_workers_do_not_both_claim_the_same_turn()
    {
        await using var scenario = await CreateScenarioAsync();
        await scenario.MakeStaleAnswerTurnAsync();
        await scenario.MakeRetryDueAsync();

        await Task.WhenAll(scenario.RunWorkerAsync(), scenario.RunWorkerAsync());

        Assert.Equal(TurnProcessingStatus.Completed, (await scenario.ReadTurnAsync()).ProcessingStatus);
        Assert.Equal(1, scenario.AiInvocationCounters.TargetAnalyzerCalls);
        Assert.Equal(1, scenario.AiInvocationCounters.TargetScorerCalls);
        Assert.Equal(1, await scenario.ReadCriterionScoreCountAsync());
    }

    [Fact]
    public async Task Recovery_keeps_the_audio_receipt_when_transcription_is_retried()
    {
        await using var scenario = await CreateScenarioAsync();
        scenario.FailSpeechToText();
        var original = await scenario.MakeStaleAudioTurnAsync(uploadAudio: true);

        await scenario.RunCurrentAsync();
        await scenario.MakeRetryDueAsync();
        await scenario.RunCurrentAsync();

        var recovered = await scenario.ReadTurnAsync();
        Assert.Equal(original.AudioCompletionReceivedAt, recovered.AudioCompletionReceivedAt);
        Assert.NotNull(recovered.AudioAcceptedAt);
        Assert.Equal(TurnProcessingStatus.Retry, recovered.ProcessingStatus);
        Assert.Null(recovered.AnsweredAt);
    }

    [Fact]
    public async Task A_reclaimed_turn_clears_the_stale_processing_token_before_retry()
    {
        await using var scenario = await CreateScenarioAsync();
        var original = await scenario.MakeStaleAnswerTurnAsync();

        await scenario.RunCurrentAsync();
        var recovered = await scenario.ReadTurnAsync();

        Assert.NotEqual(original.ProcessingToken, recovered.ProcessingToken);
        Assert.Null(recovered.ProcessingToken);
        Assert.Equal(TurnProcessingStatus.Retry, recovered.ProcessingStatus);
    }

    [Fact]
    public async Task Transcription_failure_increments_the_transcription_retry_counter()
    {
        await using var scenario = await CreateScenarioAsync();
        scenario.FailSpeechToText();
        await scenario.MakeStaleAudioTurnAsync(uploadAudio: true);

        await scenario.RunCurrentAsync();
        await scenario.MakeRetryDueAsync();
        await scenario.RunCurrentAsync();

        var recovered = await scenario.ReadTurnAsync();
        Assert.Equal(1, recovered.TranscriptionRetryCount);
        Assert.Equal(TurnProcessingStatus.Retry, recovered.ProcessingStatus);
        Assert.Null(recovered.ProcessingToken);
    }

    [Fact]
    public async Task Successful_manual_recovery_persists_one_queryable_audit_record()
    {
        await using var scenario = await CreateScenarioAsync();
        var failedTurn = await scenario.MakeInterruptedAnswerTurnAsync();

        scenario.AuthenticateAsLecturer();
        using var recoveryResponse = await scenario.Client.PostAsJsonAsync(
            $"/api/interview-attempts/{scenario.AttemptId}/recovery",
            new RecoverInterruptedTurnRequest(failedTurn.Id));
        recoveryResponse.EnsureSuccessStatusCode();

        var recoveredTurn = await scenario.ReadTurnAsync();
        Assert.Equal(TurnProcessingStatus.Completed, recoveredTurn.ProcessingStatus);

        scenario.AuthenticateAsAdministrator();
        using var auditResponse = await scenario.Client.GetAsync(
            $"/api/admin/audit-logs?userId={scenario.LecturerId}&action={AuditLogActions.ManualRecoverySucceeded}&targetEntity={AuditLogEntities.InterviewAttempt}");
        auditResponse.EnsureSuccessStatusCode();
        var auditRecords = await auditResponse.Content.ReadFromJsonAsync<List<AuditLogDto>>()
            ?? throw new InvalidOperationException("The audit endpoint returned no response body.");

        var matchingRecords = auditRecords.Where(record => record.TargetId == scenario.AttemptId).ToList();
        var audit = Assert.Single(matchingRecords);
        Assert.Equal(scenario.LecturerId, audit.UserId);
        Assert.Equal(AuditLogActions.ManualRecoverySucceeded, audit.Action);
        Assert.Equal(AuditLogEntities.InterviewAttempt, audit.TargetEntity);
        Assert.Equal(scenario.AttemptId, audit.TargetId);
        Assert.Contains($"\"turn\":\"{failedTurn.Id}\"", audit.Description);
        Assert.Contains("\"outcome\":\"Completed\"", audit.Description);
    }

    [Fact]
    public async Task Concurrent_start_requests_resume_one_attempt_without_creating_a_second_attempt()
    {
        await using var scenario = await CreateScenarioAsync();
        var results = await scenario.StartConcurrentlyAsync();

        Assert.All(results, result => Assert.Equal(scenario.AttemptId, result.State.AttemptId));
        Assert.All(results, result => Assert.False(result.Created));
    }

    [Fact]
    public async Task Audio_receipt_before_the_deadline_is_accepted_when_processing_starts_after_it()
    {
        await using var scenario = await CreateScenarioAsync();
        var (turnId, receiptAt) = await scenario.PrepareAudioReceiptBeforeDeadlineAsync();

        await scenario.SubmitAudioAsync(turnId, receiptAt);

        var turn = await scenario.ReadTurnAsync();
        Assert.Equal(receiptAt, turn.AudioCompletionReceivedAt);
        Assert.NotNull(turn.AudioAcceptedAt);
    }

    [Fact]
    public async Task Concurrent_retry_exhaustion_workers_fail_the_turn_once()
    {
        await using var scenario = await CreateScenarioAsync();
        await scenario.MakeRetryExhaustionTurnAsync();

        await Task.WhenAll(scenario.RunWorkerAsync(), scenario.RunWorkerAsync());

        var turn = await scenario.ReadTurnAsync();
        Assert.Equal(TurnProcessingStatus.Failed, turn.ProcessingStatus);
        Assert.Null(turn.ProcessingToken);
    }

    [Fact]
    public async Task Concurrent_submissions_for_the_same_turn_persist_one_accepted_transition()
    {
        await using var scenario = await CreateScenarioAsync();
        var turn = await scenario.ReadTurnAsync();

        var results = await scenario.SubmitConcurrentlyAsync(turn.Id, "concurrent answer");

        Assert.All(results, result => Assert.True(result.IsSuccessStatusCode));
        Assert.Single(results, result => result.Response?.Duplicate == false);
        Assert.Single(results, result => result.Response?.Duplicate == true);

        var persisted = await scenario.ReadTurnAsync();
        Assert.Equal("concurrent answer", persisted.AnswerTranscript);
        Assert.NotNull(persisted.AnsweredAt);
        Assert.Equal(TurnProcessingStatus.Completed, persisted.ProcessingStatus);
        Assert.NotNull(persisted.SubmissionResultJson);
    }

    [Fact]
    public async Task Concurrent_submission_and_expiration_persist_only_the_deadline_transition()
    {
        await using var scenario = await CreateScenarioAsync();
        var turn = await scenario.MakeDeadlineExpiredTurnAsync();

        var (submission, expiredCount) = await scenario.SubmitAndExpireConcurrentlyAsync(turn.Id);

        Assert.False(submission.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, submission.StatusCode);
        Assert.Equal(1, expiredCount);

        var persisted = await scenario.ReadTurnAsync();
        Assert.True(persisted.IsTimedOut);
        Assert.Null(persisted.AnswerTranscript);
        Assert.Null(persisted.AnsweredAt);
        Assert.Equal(TurnProcessingStatus.Completed, persisted.ProcessingStatus);
        Assert.NotNull(persisted.SubmissionResultJson);
    }

    private static async Task<Scenario> CreateScenarioAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"PostgreSQL integration tests require {ConnectionStringVariable}; no database was accessed.");

        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var database = connection.Database;
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} must specify an isolated PostgreSQL database name; no database was accessed.");
        if (!string.Equals(database, "r3_phase2_test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"PostgreSQL recovery tests require database r3_phase2_test; configured database was {database}.");

        var searchPath = (connection.SearchPath ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(schema => schema.Trim('"'))
            .ToArray();
        if (!searchPath.Any(schema => string.Equals(schema, "r3_phase2_app", StringComparison.OrdinalIgnoreCase))
            || searchPath.Any(schema => string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} must specify Search Path=r3_phase2_app,pg_catalog and must not use public.");

        var studentId = Guid.NewGuid();
        var factory = new ApiTestFactory(connectionString).AuthenticateAs(studentId, Roles.Student);
        var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var creatorId = Guid.NewGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var access = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
            await RoleSeedLock.WaitAsync();
            Role studentRole;
            Role creatorRole;
            try
            {
                studentRole = await GetOrCreateRoleAsync(access, Roles.Student);
                creatorRole = await GetOrCreateRoleAsync(access, Roles.Lecturer);
            }
            finally
            {
                RoleSeedLock.Release();
            }

            var studentCode = $"PG{studentId:N}"[..18];
            var creatorCode = $"PG{creatorId:N}"[..18];
            access.Users.AddRange(
                new User
                {
                    Id = studentId,
                    RoleId = studentRole.Id,
                    Role = studentRole,
                    FullName = "PostgreSQL Test Student",
                    Email = $"{studentId:N}@example.test",
                    Username = $"pg-student-{studentId:N}",
                    StudentCode = studentCode,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new User
                {
                    Id = creatorId,
                    RoleId = creatorRole.Id,
                    Role = creatorRole,
                    FullName = "PostgreSQL Test Lecturer",
                    Email = $"{creatorId:N}@example.test",
                    Username = $"pg-lecturer-{creatorId:N}",
                    LecturerCode = creatorCode,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            await access.SaveChangesAsync();

            var course = new Course { Id = Guid.NewGuid(), Code = $"PG-{Guid.NewGuid():N}"[..20], Name = "PostgreSQL test" };
            course.Lecturers.Add(new CourseLecturer
            {
                CourseId = course.Id,
                LecturerId = creatorId,
                AssignedAt = DateTime.UtcNow
            });
            var session = new ExamSession
            {
                Id = sessionId,
                CourseId = course.Id,
                Course = course,
                CreatedBy = creatorId,
                Title = "Recovery test",
                StartTime = DateTime.UtcNow.AddHours(-1),
                EndTime = DateTime.UtcNow.AddHours(1),
                TimeLimitPerAnswerSeconds = 600,
                Status = ExamSessionStatus.Published,
                RubricMode = RubricMode.Legacy,
                RubricVersionId = null
            };
            var question = new Question { Id = questionId, ExamSessionId = sessionId, OrderNo = 1, Content = "Question" };
            question.Rubric = new Rubric
            {
                Id = Guid.NewGuid(),
                QuestionId = questionId,
                MaxScore = 5,
                Criteria = { new RubricCriterion { Id = Guid.NewGuid(), OrderNo = 1, Description = "Criterion", MaxPoints = 5 } }
            };
            session.Questions.Add(question);
            access.Courses.Add(course);
            access.ExamSessions.Add(session);
            await access.SaveChangesAsync();

            var interview = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            interview.Participants.Add(new AiOralExam.Modules.Interview.Domain.Participant
            {
                Id = Guid.NewGuid(),
                ExamSessionId = sessionId,
                StudentId = studentId,
                JoinedAt = DateTime.UtcNow,
                Status = AiOralExam.Modules.Interview.Domain.ParticipantStatus.Registered
            });
            await interview.SaveChangesAsync();
        }

        using var startResponse = await client.PostAsJsonAsync(
            "/api/interview-attempts", new StartAttemptRequest(sessionId));
        if (!startResponse.IsSuccessStatusCode)
        {
            var body = await startResponse.Content.ReadAsStringAsync();

            throw new InvalidOperationException(
                $"StartAttempt API failed: {(int)startResponse.StatusCode}\n{body}",
                factory.LastServerException);
        }
        var started = await startResponse.Content.ReadFromJsonAsync<AttemptStateDto>()
            ?? throw new InvalidOperationException("The authenticated test request returned no attempt state.");
        var scopeForAudio = factory.Services.CreateAsyncScope();
        return new Scenario(factory, client, scopeForAudio, started.AttemptId, creatorId, sessionId);
    }

    private static async Task<Role> GetOrCreateRoleAsync(AccessConfigDbContext access, string name)
    {
        var role = await access.Roles.SingleOrDefaultAsync(item => item.Name == name);
        if (role is not null)
            return role;

        role = new Role { Id = Guid.NewGuid(), Name = name };
        access.Roles.Add(role);
        await access.SaveChangesAsync();
        return role;
    }

    private sealed class Scenario(
        ApiTestFactory Factory,
        HttpClient Client,
        AsyncServiceScope Scope,
        Guid AttemptId,
        Guid LecturerId,
        Guid SessionId) : IAsyncDisposable
    {
        public HttpClient Client { get; } = Client;
        public Guid AttemptId { get; } = AttemptId;
        public Guid SessionId { get; } = SessionId;
        public Guid LecturerId { get; } = LecturerId;
        public TestAiInvocationCounters AiInvocationCounters => Factory.AiInvocationCounters;

        public void AuthenticateAsLecturer() => Factory.AuthenticateAs(LecturerId, Roles.Lecturer);

        public void FailSpeechToText() => Factory.FailSpeechToText();

        public void AuthenticateAsAdministrator() => Factory.AuthenticateAs(Guid.NewGuid(), Roles.Administrator);

        public async Task<(AttemptStateDto State, bool Created)[]> StartConcurrentlyAsync()
        {
            var ready = new CountdownEvent(2);
            var tasks = Enumerable.Range(0, 2).Select(async _ =>
            {
                await Task.Run(ready.Signal);
                await Task.Run(ready.Wait);
                using var response = await Client.PostAsJsonAsync(
                    "/api/interview-attempts", new StartAttemptRequest(SessionId));
                var state = await response.Content.ReadFromJsonAsync<AttemptStateDto>()
                    ?? throw new InvalidOperationException("The concurrent start request returned no attempt state.");
                return (state, response.StatusCode == HttpStatusCode.Created);
            }).ToArray();
            return await Task.WhenAll(tasks);
        }

        public async Task<SubmitRaceResult[]> SubmitConcurrentlyAsync(Guid turnId, string answer)
        {
            using var ready = new CountdownEvent(2);
            var tasks = Enumerable.Range(0, 2).Select(async _ =>
            {
                await Task.Run(ready.Signal);
                await Task.Run(ready.Wait);
                using var response = await Client.PostAsJsonAsync(
                    $"/api/interview-attempts/{AttemptId}/responses",
                    new SubmitAnswerRequest(answer, turnId));
                var result = response.IsSuccessStatusCode
                    ? await response.Content.ReadFromJsonAsync<SubmitAnswerResponse>()
                    : null;
                return new SubmitRaceResult(response.StatusCode, result);
            }).ToArray();
            return await Task.WhenAll(tasks);
        }

        public async Task<InterviewTurn> MakeDeadlineExpiredTurnAsync()
        {
            await UpdateTurnAsync(turn => turn.AskedAt = DateTime.UtcNow.AddSeconds(-620));
            return await ReadTurnAsync();
        }

        public async Task<(SubmitRaceResult Submission, int ExpiredCount)> SubmitAndExpireConcurrentlyAsync(Guid turnId)
        {
            using var ready = new CountdownEvent(2);
            var submitTask = Task.Run(async () =>
            {
                await Task.Run(ready.Signal);
                await Task.Run(ready.Wait);
                using var response = await Client.PostAsJsonAsync(
                    $"/api/interview-attempts/{AttemptId}/responses",
                    new SubmitAnswerRequest("late answer", turnId));
                return new SubmitRaceResult(response.StatusCode,
                    response.IsSuccessStatusCode
                        ? await response.Content.ReadFromJsonAsync<SubmitAnswerResponse>()
                        : null);
            });
            var expirationTask = Task.Run(async () =>
            {
                await Task.Run(ready.Signal);
                await Task.Run(ready.Wait);
                return await RunWorkerAsync();
            });
            return (await submitTask, await expirationTask);
        }

        public async Task<int> RunCurrentAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IInterviewService>().ExpireOverdueAttemptsAsync();
        }

        public async Task<InterviewTurn> MakeStaleAnswerTurnAsync()
        {
            var token = Guid.NewGuid();
            var answerMarker = $"stale answer {AttemptId:N}";
            await UpdateTurnAsync(turn =>
            {
                turn.AnswerTranscript = answerMarker;
                turn.AnsweredAt = DateTime.UtcNow.AddMinutes(-3);
                turn.ProcessingStatus = TurnProcessingStatus.Processing;
                turn.ProcessingStartedAt = DateTime.UtcNow.AddMinutes(-3);
                turn.ProcessingToken = token;
            });
            Factory.AiInvocationCounters.SetTargetAnswerMarker(answerMarker);
            return await ReadTurnAsync();
        }

        public async Task<(Guid TurnId, DateTime ReceiptAt)> PrepareAudioReceiptBeforeDeadlineAsync()
        {
            var turn = await ReadTurnAsync();
            var store = Scope.ServiceProvider.GetRequiredService<IAudioChunkStore>();
            await store.AppendAsync(AttemptId, turn.Id, 0, [1, 2, 3], "audio/wav", CancellationToken.None);
            var completed = await store.CompleteUploadAsync(AttemptId, turn.Id, CancellationToken.None);
            await completed.Audio.DisposeAsync();

            var askedAt = DateTime.UtcNow.AddSeconds(-620);
            var receiptAt = DateTime.UtcNow.AddSeconds(-20);
            await UpdateTurnAsync(current => current.AskedAt = askedAt);
            return (turn.Id, receiptAt);
        }

        public async Task MakeRetryExhaustionTurnAsync()
        {
            await UpdateTurnAsync(turn =>
            {
                turn.AnswerTranscript = $"retry exhaustion {AttemptId:N}";
                turn.AnsweredAt = DateTime.UtcNow.AddMinutes(-3);
                turn.ProcessingStatus = TurnProcessingStatus.Processing;
                turn.ProcessingStartedAt = DateTime.UtcNow.AddMinutes(-3);
                turn.ProcessingToken = Guid.NewGuid();
                turn.RetryCount = 3;
            });
        }

        public async Task<SubmitAnswerResponse> SubmitAudioAsync(Guid turnId, DateTime receiptAt)
        {
            var accessor = Factory.Services.GetRequiredService<IHttpContextAccessor>();
            var previous = accessor.HttpContext;
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(AppClaims.UserId, Factory.AuthenticatedUserId.ToString()),
                    new Claim(AppClaims.Role, Roles.Student)
                ], TestAuthenticationHandler.SchemeName, AppClaims.Name, AppClaims.Role))
            };
            accessor.HttpContext = context;
            try
            {
                await using var scope = Factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IInterviewService>()
                    .SubmitAudioAnswerAsync(AttemptId, turnId, receiptAt);
            }
            finally
            {
                accessor.HttpContext = previous;
            }
        }

        public async Task<InterviewTurn> MakeInterruptedAnswerTurnAsync()
        {
            await UpdateTurnAsync(turn =>
            {
                turn.AnswerTranscript = "recoverable answer";
                turn.AnsweredAt = DateTime.UtcNow.AddMinutes(-3);
                turn.ProcessingStatus = TurnProcessingStatus.Failed;
                turn.RetryCount = 3;
            }, interruptAttempt: true);
            return await ReadTurnAsync();
        }

        public async Task<InterviewTurn> MakeStaleAudioTurnAsync(bool uploadAudio = false)
        {
            var receipt = DateTime.UtcNow.AddMinutes(-3);
            if (uploadAudio)
            {
                var store = Scope.ServiceProvider.GetRequiredService<IAudioChunkStore>();
                var turn = await ReadTurnAsync();
                await store.AppendAsync(AttemptId, turn.Id, 0, [1, 2, 3], "audio/wav", CancellationToken.None);
                var completed = await store.CompleteUploadAsync(AttemptId, turn.Id, CancellationToken.None);
                await completed.Audio.DisposeAsync();
            }
            await UpdateTurnAsync(turn =>
            {
                turn.AudioCompletionReceivedAt = receipt;
                turn.ProcessingStatus = TurnProcessingStatus.Processing;
                turn.ProcessingStartedAt = DateTime.UtcNow.AddMinutes(-3);
                turn.ProcessingToken = Guid.NewGuid();
            });
            return await ReadTurnAsync();
        }

        public Task MakeRetryDueAsync() =>
            UpdateTurnAsync(turn =>
            {
                turn.ProcessingStatus = TurnProcessingStatus.Retry;
                turn.ProcessingToken = null;
                turn.NextRetryAt = DateTime.UtcNow.AddSeconds(-1);
            });

        public async Task<int> RunWorkerAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IInterviewService>().ExpireOverdueAttemptsAsync();
        }

        public async Task<InterviewTurn> ReadTurnAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            var responseId = await db.QuestionResponses.Where(response => response.AttemptId == AttemptId)
                .Select(response => response.Id).SingleAsync();
            return await db.InterviewTurns.SingleAsync(turn => turn.QuestionResponseId == responseId);
        }

        public async Task<int> ReadCriterionScoreCountAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            var responseId = await db.QuestionResponses.Where(response => response.AttemptId == AttemptId)
                .Select(response => response.Id).SingleAsync();
            return await db.CriterionScores.CountAsync(score => score.QuestionResponseId == responseId);
        }

        private async Task UpdateTurnAsync(Action<InterviewTurn> update, bool interruptAttempt = false)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            var responseId = await db.QuestionResponses.Where(response => response.AttemptId == AttemptId)
                .Select(response => response.Id).SingleAsync();
            var turn = await db.InterviewTurns.SingleAsync(item => item.QuestionResponseId == responseId);
            update(turn);
            if (interruptAttempt)
                (await db.InterviewAttempts.SingleAsync(attempt => attempt.Id == AttemptId)).Interrupt(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Scope.DisposeAsync();
            await Factory.DisposeAsync();
        }

        public sealed record SubmitRaceResult(HttpStatusCode StatusCode, SubmitAnswerResponse? Response)
        {
            public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and < 300;
        }
    }

    [CollectionDefinition(nameof(PostgresInterviewRecoveryTestsCollection))]
    public sealed class PostgresInterviewRecoveryTestsCollection
    {
    }
}
