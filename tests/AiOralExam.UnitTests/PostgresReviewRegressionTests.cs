using AiOralExam.Modules.AccessConfig.Application.QuestionBank;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Application.Audio;
using AiOralExam.Modules.Interview.Application.Events;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
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

        var examConfig = new FixedExamConfigurationApi(session);
            examConfig.SetIds(attemptId, turnId, responseId);
            var analyzer = new GatedAnswerAnalyzer();
            await using var firstDb = CreateDbContext(connectionString);
            await using var secondDb = CreateDbContext(connectionString);
            var first = CreateService(firstDb, examConfig, analyzer, studentId);
            var second = CreateService(secondDb, examConfig, analyzer, studentId);
            var request = new SubmitAnswerRequest("The answer", turnId);

            var firstTask = first.SubmitAnswerAsync(attemptId, request);
            await analyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var duplicate = await second.SubmitAnswerAsync(attemptId, request).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(duplicate.Duplicate);
            Assert.Equal(1, analyzer.CallCount);

            analyzer.Release.TrySetResult();
            var result = await firstTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(SubmitOutcome.Completed, result.Outcome);

            await using var verify = CreateDbContext(connectionString);
            var response = await verify.QuestionResponses.AsNoTracking().SingleAsync(x => x.Id == responseId);
            Assert.Equal(AiEvaluationState.Completed, response.AiEvaluationState);
            Assert.Null(response.AiEvaluationClaimToken);
            Assert.Equal(1, analyzer.CallCount);
        });
    }

    [PostgresFact]
    public async Task CancelledEvaluationReleasesItsClaimAndAllowsRetry()
    {
        var sessionId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var examConfig = new FixedExamConfigurationApi(CreateSession(sessionId, questionId));

        await WithInterviewSchemaAsync(async connectionString =>
        {
            await using (var setup = CreateDbContext(connectionString))
            {
                await CreateSchemaTablesAsync(setup);
                var ids = await SeedOpenAttemptAsync(setup, sessionId, studentId, questionId);
                examConfig.SetIds(ids.AttemptId, ids.TurnId, ids.ResponseId);
            }

            var analyzer = new GatedAnswerAnalyzer();
            var options = new InterviewOptions { InitialTurnRetryDelaySeconds = 0, MaximumTurnRetryDelaySeconds = 0 };
            using var cancellation = new CancellationTokenSource();
            await using (var db = CreateDbContext(connectionString))
            {
                var service = CreateService(db, examConfig, analyzer, studentId, interviewOptions: options);
                var submission = service.SubmitAnswerAsync(
                    examConfig.AttemptId,
                    new SubmitAnswerRequest("The answer", examConfig.TurnId),
                    cancellation.Token);
                await analyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submission);
            }

            await using (var verify = CreateDbContext(connectionString))
            {
                var response = await verify.QuestionResponses.AsNoTracking().SingleAsync(x => x.Id == examConfig.ResponseId);
                var turn = await verify.InterviewTurns.AsNoTracking().SingleAsync(x => x.Id == examConfig.TurnId);
                Assert.Equal(AiEvaluationState.Failed, response.AiEvaluationState);
                Assert.Null(response.AiEvaluationClaimToken);
                Assert.Null(response.AiEvaluationClaimedAt);
                Assert.Equal("The answer", turn.AnswerTranscript);
                Assert.NotNull(turn.AnsweredAt);
                Assert.Equal(TurnProcessingStatus.Retry, turn.ProcessingStatus);
            }

            analyzer.Release.TrySetResult();
            await using var retryDb = CreateDbContext(connectionString);
            var retryService = CreateService(retryDb, examConfig, analyzer, studentId, interviewOptions: options);
            var retry = await retryService.SubmitAnswerAsync(
                examConfig.AttemptId,
                new SubmitAnswerRequest("The answer", examConfig.TurnId));
            Assert.Equal(SubmitOutcome.Completed, retry.Outcome);
            Assert.Equal(2, analyzer.CallCount);
        });
    }

    [PostgresFact]
    public async Task ExpiredClaimTakeoverPreventsStillRunningOwnerFromOverwritingEvaluation()
    {
        var sessionId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var criterionId = Guid.NewGuid();
        var criterion = new RubricCriterionInfo(criterionId, 1, "Accuracy", 5);
        var examConfig = new FixedExamConfigurationApi(CreateSession(sessionId, questionId, criterion));

        await WithInterviewSchemaAsync(async connectionString =>
        {
            await using (var setup = CreateDbContext(connectionString))
            {
                await CreateSchemaTablesAsync(setup);
                var ids = await SeedOpenAttemptAsync(setup, sessionId, studentId, questionId);
                examConfig.SetIds(ids.AttemptId, ids.TurnId, ids.ResponseId);
            }

            var analyzer = new OverlappingAnswerAnalyzer();
            var scorer = new OverlappingRubricScorer(criterionId);
            await using var staleDb = CreateDbContext(connectionString);
            var staleService = CreateService(staleDb, examConfig, analyzer, studentId, scorer);
            var staleRequest = staleService.SubmitAnswerAsync(
                examConfig.AttemptId,
                new SubmitAnswerRequest("stale answer", examConfig.TurnId));
            await analyzer.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Simulate the lease expiring while request A is still blocked in its provider call.
            var options = new InterviewOptions { InitialTurnRetryDelaySeconds = 0, MaximumTurnRetryDelaySeconds = 0 };
            await using (var expireLeaseDb = CreateDbContext(connectionString))
            {
                var response = await expireLeaseDb.QuestionResponses.SingleAsync(x => x.Id == examConfig.ResponseId);
                Assert.Equal(AiEvaluationState.Pending, response.AiEvaluationState);
                Assert.NotNull(response.AiEvaluationClaimToken);
                var turn = await expireLeaseDb.InterviewTurns.SingleAsync(x => x.Id == examConfig.TurnId);
                turn.ProcessingStartedAt = DateTime.UtcNow.AddHours(-1);
                await expireLeaseDb.SaveChangesAsync();
            }

            // The recovery worker expires A's turn lease; request B then retries and takes ownership.
            await using (var winnerDb = CreateDbContext(connectionString))
            {
                var winnerService = CreateService(winnerDb, examConfig, analyzer, studentId, scorer, options);
                await winnerService.ExpireOverdueAttemptsAsync();
                var winner = await winnerService.SubmitAnswerAsync(
                    examConfig.AttemptId,
                    new SubmitAnswerRequest("stale answer", examConfig.TurnId));
                Assert.Equal(SubmitOutcome.Completed, winner.Outcome);
            }

            analyzer.ReleaseFirstRequest.TrySetResult();
            var staleResult = await staleRequest;
            Assert.True(staleResult.Duplicate);
            Assert.Equal(SubmitOutcome.Completed, staleResult.Outcome);

            await using var verify = CreateDbContext(connectionString);
            var persistedResponse = await verify.QuestionResponses.AsNoTracking()
                .SingleAsync(x => x.Id == examConfig.ResponseId);
            Assert.Equal(AiEvaluationState.Completed, persistedResponse.AiEvaluationState);
            Assert.True(persistedResponse.IsFinished);
            Assert.Equal(4, persistedResponse.AiSuggestedScore);
            Assert.Equal("winner feedback", persistedResponse.AiFeedback);
            Assert.Null(persistedResponse.AiEvaluationClaimToken);
            Assert.Null(persistedResponse.AiEvaluationClaimedAt);

            var persistedScores = await verify.CriterionScores.AsNoTracking()
                .Where(x => x.QuestionResponseId == examConfig.ResponseId)
                .ToListAsync();
            var persistedScore = Assert.Single(persistedScores);
            Assert.Equal(criterionId, persistedScore.RubricCriterionId);
            Assert.Equal(4, persistedScore.Points);
            Assert.Equal("winner", persistedScore.Comment);

            var persistedTurn = await verify.InterviewTurns.AsNoTracking().SingleAsync(x => x.Id == examConfig.TurnId);
            Assert.Equal("stale answer", persistedTurn.AnswerTranscript);
            Assert.NotNull(persistedTurn.AnsweredAt);
            Assert.Equal(TurnProcessingStatus.Completed, persistedTurn.ProcessingStatus);
            Assert.Equal(1, await verify.InterviewTurns.CountAsync(x => x.QuestionResponseId == examConfig.ResponseId));

            var persistedAttempt = await verify.InterviewAttempts.AsNoTracking()
                .SingleAsync(x => x.Id == examConfig.AttemptId);
            Assert.Equal(AttemptStatus.PendingReview, persistedAttempt.Status);
            Assert.NotNull(persistedAttempt.CompletedAt);
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

    private static InterviewDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();
        return new InterviewDbContext(options.Options);
    }

    private static Task<int> CreateSchemaTablesAsync(InterviewDbContext db) =>
        db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());

    private static InterviewService CreateService(
        InterviewDbContext db,
        IExamConfigurationApi examConfig,
        IAnswerAnalyzer analyzer,
        Guid studentId,
        IRubricScorer? scorer = null,
        InterviewOptions? interviewOptions = null) => new(
        db,
        examConfig,
        analyzer,
        scorer ?? new EmptyRubricScorer(),
        new NoopAudioChunkStore(),
        new NoopSpeechToTextService(),
        new NoopInterviewEventPublisher(),
        new NoopAuditLogWriter(),
        new TestCurrentUser(studentId),
        TimeProvider.System,
        Options.Create(interviewOptions ?? new InterviewOptions()),
        Options.Create(new AiProviderOptions()),
        NullLogger<InterviewService>.Instance);

    private static ExamSessionInfo CreateSession(Guid sessionId, Guid questionId, RubricCriterionInfo? criterion = null) => new(
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
        [new QuestionInfo(questionId, 1, "Explain the answer.", criterion is null ? [] : [criterion], criterion?.MaxPoints ?? 0)]);

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

    private sealed class OverlappingAnswerAnalyzer : IAnswerAnalyzer
    {
        private int _callCount;
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }

            return new AnswerAnalysis(true, [], false, null);
        }
    }

    private sealed class OverlappingRubricScorer(Guid criterionId) : IRubricScorer
    {
        private int _callCount;

        public Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default)
        {
            var isWinner = Interlocked.Increment(ref _callCount) == 1;
            var score = isWinner
                ? new RubricScore([new CriterionPoints(criterionId, 4, "winner")], "winner feedback")
                : new RubricScore([new CriterionPoints(criterionId, 1, "stale")], "stale feedback");
            return Task.FromResult(score);
        }
    }

    private sealed class EmptyRubricScorer : IRubricScorer
    {
        public Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default) =>
            Task.FromResult(new RubricScore([], "Scored."));
    }

    private sealed class NoopAudioChunkStore : IAudioChunkStore
    {
        public Task<AudioChunkAppendResult> AppendAsync(Guid attemptId, Guid turnId, int sequence, byte[] chunk,
            string contentType, CancellationToken ct) => throw new NotSupportedException();

        public Task<AudioChunkCheckpoint?> GetCheckpointAsync(Guid attemptId, Guid turnId, CancellationToken ct) =>
            Task.FromResult<AudioChunkCheckpoint?>(null);

        public Task<CompletedAudio> CompleteUploadAsync(Guid attemptId, Guid turnId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> SaveTranscriptIfAbsentAsync(Guid attemptId, Guid turnId, string transcript, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NoopSpeechToTextService : ISpeechToTextService
    {
        public Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopInterviewEventPublisher : IInterviewEventPublisher
    {
        public Task PublishAsync(InterviewAttemptEvent interviewEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishTranscriptUpdatedAsync(TranscriptUpdatedEvent transcriptEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopAuditLogWriter : IAuditLogWriter
    {
        public Task AppendAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
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
