using AiOralExam.Modules.AccessConfig.Application.QuestionBank;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AiOralExam.UnitTests;

public sealed class PostgresReviewRegressionTests
{
    private const string ConnectionEnvironmentVariable = "AIVES_REVIEW_POSTGRES";

    [PostgresFact]
    public async Task ConcurrentSubmissions_ToTheSameTurnOnlyStartOneEvaluation()
    {
        var sessionId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var session = CreateSession(sessionId, questionId);
        await WithInterviewSchemaAsync(async connectionString =>
        {
            (var attemptId, var turnId, var responseId) = default((Guid, Guid, Guid));
            await using (var setup = CreateDbContext(connectionString))
            {
                await CreateSchemaTablesAsync(setup);
                (attemptId, turnId, responseId) = await SeedOpenAttemptAsync(setup, sessionId, studentId, questionId);
            }

            var examConfig = new TwoRequestExamConfigurationApi(session);
            examConfig.SetIds(attemptId, turnId, responseId);
            var analyzer = new GatedAnswerAnalyzer();
            await using var firstDb = CreateDbContext(connectionString);
            await using var secondDb = CreateDbContext(connectionString);
            var first = CreateService(firstDb, examConfig, analyzer, studentId);
            var second = CreateService(secondDb, examConfig, analyzer, studentId);
            var request = new SubmitAnswerRequest("The answer", turnId);

            var firstTask = first.SubmitAnswerAsync(attemptId, request);
            var secondTask = second.SubmitAnswerAsync(attemptId, request);
            await analyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var rejectedTask = await Task.WhenAny(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<ConflictException>(() => rejectedTask);
            Assert.Equal(1, analyzer.CallCount);

            analyzer.Release.TrySetResult();
            var acceptedTask = ReferenceEquals(rejectedTask, firstTask) ? secondTask : firstTask;
            var result = await acceptedTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(SubmitOutcome.Completed, result.Outcome);

            await using var verify = CreateDbContext(connectionString);
            var response = await verify.QuestionResponses.AsNoTracking().SingleAsync(x => x.Id == responseId);
            Assert.Equal(AiEvaluationState.Completed, response.AiEvaluationState);
            Assert.Null(response.AiEvaluationClaimToken);
            Assert.Equal(1, analyzer.CallCount);
        });
    }

    [PostgresFact]
    public async Task FailedTranscriptSaveCannotReleaseAClaimOwnedByAnotherRequest()
    {
        var sessionId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var session = CreateSession(sessionId, questionId);
        var replacementToken = Guid.NewGuid();
        var examConfig = new FixedExamConfigurationApi(session);

        await WithInterviewSchemaAsync(async connectionString =>
        {
            await using (var setup = CreateDbContext(connectionString))
            {
                await CreateSchemaTablesAsync(setup);
                var ids = await SeedOpenAttemptAsync(setup, sessionId, studentId, questionId);
                examConfig.SetIds(ids.AttemptId, ids.TurnId, ids.ResponseId);
            }

            var analyzer = new GatedAnswerAnalyzer();
            var interceptor = new ReplaceClaimTokenAndFailSaveInterceptor(
                connectionString, examConfig.ResponseId, replacementToken);
            await using var db = CreateDbContext(connectionString, interceptor);
            var service = CreateService(db, examConfig, analyzer, studentId);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SubmitAnswerAsync(examConfig.AttemptId,
                    new SubmitAnswerRequest("The answer", examConfig.TurnId)));

            await using var verify = CreateDbContext(connectionString);
            var response = await verify.QuestionResponses.AsNoTracking().SingleAsync(x => x.Id == examConfig.ResponseId);
            Assert.Equal(AiEvaluationState.Pending, response.AiEvaluationState);
            Assert.Equal(replacementToken, response.AiEvaluationClaimToken);
            Assert.Equal(0, analyzer.CallCount);
        });
    }

    [PostgresFact]
    public async Task CriterionScoresEnforceOneScorePerResponseAndCriterionInPostgres()
    {
        var sessionId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var questionId = Guid.NewGuid();

        await WithInterviewSchemaAsync(async connectionString =>
        {
            await using var db = CreateDbContext(connectionString);
            await CreateSchemaTablesAsync(db);
            var (_, _, responseId) = await SeedOpenAttemptAsync(db, sessionId, studentId, questionId);
            var criterionId = Guid.NewGuid();
            db.CriterionScores.AddRange(
                new CriterionScore { Id = Guid.NewGuid(), QuestionResponseId = responseId, RubricCriterionId = criterionId, Points = 1 },
                new CriterionScore { Id = Guid.NewGuid(), QuestionResponseId = responseId, RubricCriterionId = criterionId, Points = 2 });

            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("ux_criterion_scores_response_criterion", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        });
    }

    [PostgresFact]
    public async Task ContentHashBackfillExpressionMatchesApplicationHashForVietnameseAndUnicode()
    {
        var connectionString = GetConnectionString();
        var samples = new[]
        {
            "  Mô tả\t câu hỏi tiếng Việt  ",
            "\u00a0Đánh giá\u2003câu hỏi\u00a0",
            "  Tiếng Việt\r\nUnicode  ",
            "ß Straße İstanbul",
        };
        var persistedContents = samples.Select(content => content.Trim()).ToArray();

        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new AccessConfigDbContext(options);
        var applicationHashes = await QuestionBankContentHash.ComputeManyAsync(db, persistedContents, CancellationToken.None);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        for (var i = 0; i < samples.Length; i++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT encode(sha256(convert_to(upper(btrim(regexp_replace(@content, '\\s+', ' ', 'g'))), 'UTF8')), 'hex')";
            command.Parameters.AddWithValue("content", persistedContents[i]);
            var databaseHash = (string?)await command.ExecuteScalarAsync();
            Assert.True(applicationHashes[i] == databaseHash, $"Hash mismatch for input {samples[i]}");
        }
    }

    private static InterviewDbContext CreateDbContext(string connectionString, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new InterviewDbContext(options.Options);
    }

    private static Task<int> CreateSchemaTablesAsync(InterviewDbContext db) =>
        db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());

    private static InterviewService CreateService(
        InterviewDbContext db,
        IExamConfigurationApi examConfig,
        IAnswerAnalyzer analyzer,
        Guid studentId) => new(
        db,
        examConfig,
        analyzer,
        new EmptyRubricScorer(),
        new TestCurrentUser(studentId),
        TimeProvider.System,
        Options.Create(new InterviewOptions()),
        NullLogger<InterviewService>.Instance);

    private static ExamSessionInfo CreateSession(Guid sessionId, Guid questionId) => new(
        sessionId,
        Guid.NewGuid(),
        "TEST",
        "Test course",
        "PostgreSQL claim test",
        DateTime.UtcNow.AddHours(-1),
        DateTime.UtcNow.AddHours(1),
        3600,
        0,
        ExamSessionStatus.Published,
        [new QuestionInfo(questionId, 1, "Explain the answer.", [], 0)]);

    private static async Task<(Guid AttemptId, Guid TurnId, Guid ResponseId)> SeedOpenAttemptAsync(
        InterviewDbContext db,
        Guid sessionId,
        Guid studentId,
        Guid questionId)
    {
        var now = DateTime.UtcNow;
        var participant = new Participant
        {
            Id = Guid.NewGuid(), ExamSessionId = sessionId, StudentId = studentId,
            JoinedAt = now, Status = ParticipantStatus.InProgress,
        };
        var attempt = new InterviewAttempt { Id = Guid.NewGuid(), ParticipantId = participant.Id, Participant = participant, StartedAt = now };
        var response = new QuestionResponse
        {
            Id = Guid.NewGuid(), AttemptId = attempt.Id, QuestionId = questionId,
            AiEvaluationState = AiEvaluationState.NotStarted,
        };
        var turn = response.AddTurn(TurnType.Main, "Explain the answer.", now);
        attempt.Responses.Add(response);
        participant.Attempts.Add(attempt);
        db.Participants.Add(participant);
        await db.SaveChangesAsync();
        return (attempt.Id, turn.Id, response.Id);
    }

    private static async Task<T> WithInterviewSchemaAsync<T>(Func<string, Task<T>> run)
    {
        var adminConnectionString = GetConnectionString();
        var schema = $"pr8_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        var scopedConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { SearchPath = schema }.ConnectionString;
        try
        {
            return await run(scopedConnectionString);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static Task WithInterviewSchemaAsync(Func<string, Task> run) =>
        WithInterviewSchemaAsync(async connectionString =>
        {
            await run(connectionString);
            return true;
        });

    private static string GetConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        throw new InvalidOperationException($"{nameof(PostgresFactAttribute)} must skip tests without {ConnectionEnvironmentVariable}.");
    }

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => "Student";
        public bool IsInRole(string role) => role == Role;
    }

    private class FixedExamConfigurationApi(ExamSessionInfo session) : IExamConfigurationApi
    {
        public Guid AttemptId { get; private set; }
        public Guid TurnId { get; protected set; }
        public Guid ResponseId { get; protected set; }

        public virtual Task<ExamSessionInfo?> GetSessionAsync(Guid examSessionId, CancellationToken ct = default) => Task.FromResult<ExamSessionInfo?>(session);
        public Task<IReadOnlyList<ExamSessionInfo>> GetSessionsAsync(IReadOnlyCollection<Guid> examSessionIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ExamSessionInfo>>([session]);
        public Task<bool> IsLecturerOfSessionAsync(Guid lecturerId, Guid examSessionId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<UserBasicInfo>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UserBasicInfo>>([]);
        public Task<UserAccessInfo?> GetUserAccessAsync(Guid userId, CancellationToken ct = default) => Task.FromResult<UserAccessInfo?>(null);

        public void SetIds(Guid attemptId, Guid turnId, Guid responseId)
        {
            AttemptId = attemptId;
            TurnId = turnId;
            ResponseId = responseId;
        }
    }

    private sealed class TwoRequestExamConfigurationApi(ExamSessionInfo session) : FixedExamConfigurationApi(session)
    {
        private readonly TaskCompletionSource _bothRequestsLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public override async Task<ExamSessionInfo?> GetSessionAsync(Guid examSessionId, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 2) _bothRequestsLoaded.TrySetResult();
            await _bothRequestsLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return await base.GetSessionAsync(examSessionId, ct);
        }
    }

    private sealed class GatedAnswerAnalyzer : IAnswerAnalyzer
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new AnswerAnalysis(true, [], false, null);
        }
    }

    private sealed class EmptyRubricScorer : IRubricScorer
    {
        public Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default) =>
            Task.FromResult(new RubricScore([], "Scored."));
    }

    private sealed class ReplaceClaimTokenAndFailSaveInterceptor(
        string connectionString,
        Guid responseId,
        Guid replacementToken) : SaveChangesInterceptor
    {
        private int _ran;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _ran, 1) == 0)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("UPDATE question_responses SET ai_evaluation_claim_token = @replacement WHERE id = @response AND ai_evaluation_claim_token IS NOT NULL", connection);
                command.Parameters.AddWithValue("replacement", replacementToken);
                command.Parameters.AddWithValue("response", responseId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
                throw new InvalidOperationException("Simulated transcript persistence failure.");
            }
            return result;
        }
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIVES_REVIEW_POSTGRES")))
            Skip = "Set AIVES_REVIEW_POSTGRES to an isolated PostgreSQL test database to run this test.";
    }
}
