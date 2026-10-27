using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Application.Events;
using AiOralExam.Modules.Interview.Application.Features.ScoreReview;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOralExam.UnitTests;

public sealed class InterviewTurnWorkflowTests
{
    private static readonly DateTime StartTime = new(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task First_answer_is_saved_and_turn_id_retry_is_idempotent()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = Assert.IsType<Guid>(started.State.CurrentTurn?.TurnId);

        var first = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("A clear answer", turnId));
        var retry = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("A clear answer", turnId));

        Assert.False(first.Duplicate);
        Assert.True(retry.Duplicate);
        Assert.Equal(first.Attempt.AttemptId, retry.Attempt.AttemptId);
        Assert.Equal(AttemptStatus.PendingReview.ToString(), retry.Attempt.Status);
        Assert.Single(fixture.Db.InterviewAttempts);
        Assert.Single(fixture.Db.QuestionResponses);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Equal(1, fixture.Analyzer.CallCount);
        Assert.Equal(1, fixture.Scorer.CallCount);
        Assert.Equal(["attempt.started", "turn.opened", "answer.accepted", "attempt.completed", "answer.duplicate"],
            fixture.Events.Events.Select(interviewEvent => interviewEvent.Type));
        Assert.Equal([1L, 2L, 3L, 4L, 4L], fixture.Events.Events.Select(interviewEvent => interviewEvent.Sequence));
        Assert.All(fixture.Events.Events, interviewEvent =>
            Assert.Equal(new[] { "AttemptId", "EventId", "ExamSessionId", "OccurredAt", "Sequence", "StateVersion", "TurnId", "Type" },
                typeof(InterviewAttemptEvent).GetProperties().Select(property => property.Name).OrderBy(name => name)));
        Assert.DoesNotContain("AnswerTranscript", typeof(InterviewAttemptEvent).GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("AiSuggestedScore", typeof(InterviewAttemptEvent).GetProperties().Select(property => property.Name));
        Assert.All(fixture.Events.Events.Where(interviewEvent => interviewEvent.Type != "review.finalized"),
            interviewEvent => Assert.Equal(interviewEvent.Sequence, interviewEvent.StateVersion));
    }

    [Fact]
    public async Task Delayed_identical_retry_returns_the_original_turn_result_after_the_attempt_advances()
    {
        await using var fixture = await CreateFixtureAsync(maxFollowUps: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var mainTurnId = started.State.CurrentTurn!.TurnId;
        fixture.Analyzer.NeedsFollowUp = true;

        var original = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Main answer", mainTurnId));
        Assert.Equal(SubmitOutcome.FollowUp, original.Outcome);
        var originalFollowUpId = original.Attempt.CurrentTurn!.TurnId;

        fixture.Analyzer.NeedsFollowUp = false;
        await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Follow-up answer", originalFollowUpId));
        var retry = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Main answer", mainTurnId));

        Assert.True(retry.Duplicate);
        Assert.Equal(original.Outcome, retry.Outcome);
        Assert.Equal(original.Attempt.CurrentTurn?.TurnId, retry.Attempt.CurrentTurn?.TurnId);
        Assert.Equal(2, fixture.Analyzer.CallCount);
        Assert.Single(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Scorer_failure_is_recovered_by_the_worker_without_losing_the_answer()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Scorer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Persist before score", started.State.CurrentTurn!.TurnId)));

        Assert.Equal("Persist before score", fixture.Db.InterviewTurns.Single().AnswerTranscript);
        Assert.Equal(TurnProcessingStatus.Retry, fixture.Db.InterviewTurns.Single().ProcessingStatus);
        Assert.Empty(fixture.Db.CriterionScores);

        Assert.Equal(1, await fixture.Service.ExpireOverdueAttemptsAsync());

        Assert.Equal(TurnProcessingStatus.Completed, fixture.Db.InterviewTurns.Single().ProcessingStatus);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Equal(2, fixture.Scorer.CallCount);
    }

    [Fact]
    public async Task Missing_or_empty_turn_id_is_rejected_without_falling_back_to_current_turn()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var error = await Assert.ThrowsAsync<ValidationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Answer", Guid.Empty)));

        Assert.Contains(nameof(SubmitAnswerRequest.TurnId), error.Details!.Keys);
        Assert.Null(fixture.Db.InterviewTurns.Single().AnsweredAt);
        Assert.Equal(0, fixture.Analyzer.CallCount);
    }

    [Fact]
    public async Task Get_state_reconciles_an_expired_turn_when_the_worker_has_not_run()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(2));

        var state = await fixture.Service.GetAttemptStateAsync(started.State.AttemptId);

        Assert.Null(state.CurrentTurn);
        Assert.Equal(AttemptStatus.PendingReview.ToString(), state.Status);
        Assert.True(fixture.Db.InterviewTurns.Single().IsTimedOut);
        Assert.Null(fixture.Db.InterviewTurns.Single().AnsweredAt);
    }

    [Fact]
    public async Task Analyzer_failure_keeps_answer_and_identical_retry_recovers_processing()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        fixture.Analyzer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Durable answer", turnId)));

        var turn = Assert.Single(fixture.Db.InterviewTurns);
        Assert.Equal("Durable answer", turn.AnswerTranscript);
        Assert.NotNull(turn.AnsweredAt);
        Assert.Equal(TurnProcessingStatus.Retry, turn.ProcessingStatus);
        Assert.Empty(fixture.Db.CriterionScores);

        var retry = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Durable answer", turnId));

        Assert.Equal(SubmitOutcome.Completed, retry.Outcome);
        Assert.Equal(TurnProcessingStatus.Completed, turn.ProcessingStatus);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Equal(2, fixture.Analyzer.CallCount);
        Assert.Equal(1, fixture.Scorer.CallCount);
    }

    [Fact]
    public async Task Explicitly_empty_answer_is_distinct_from_unanswered_timeout()
    {
        await using var submitted = await CreateFixtureAsync();
        var started = await submitted.Service.StartAttemptAsync(new StartAttemptRequest(submitted.Session.Id));
        await submitted.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("", started.State.CurrentTurn!.TurnId));
        Assert.Equal(string.Empty, submitted.Db.InterviewTurns.Single().AnswerTranscript);
        Assert.NotNull(submitted.Db.InterviewTurns.Single().AnsweredAt);
        Assert.False(submitted.Db.InterviewTurns.Single().IsTimedOut);

        await using var timedOut = await CreateFixtureAsync(timeLimitSeconds: 1);
        var timedOutStart = await timedOut.Service.StartAttemptAsync(new StartAttemptRequest(timedOut.Session.Id));
        timedOut.Clock.Set(StartTime.AddSeconds(2));
        await timedOut.Service.ExpireOverdueAttemptsAsync();
        var expiredTurn = Assert.Single(timedOut.Db.InterviewTurns);
        Assert.Null(expiredTurn.AnswerTranscript);
        Assert.Null(expiredTurn.AnsweredAt);
        Assert.True(expiredTurn.IsTimedOut);
        Assert.Null((await timedOut.Service.GetAttemptStateAsync(timedOutStart.State.AttemptId)).CurrentTurn);
    }

    [Fact]
    public async Task Same_turn_id_with_different_answer_is_rejected_without_mutation()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("First answer", turnId));

        var error = await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Different answer", turnId)));

        Assert.Equal("turn_submission_conflict", error.Code);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Single(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Next_turn_deadline_starts_after_ai_processing_opens_the_turn()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 10, questionCount: 2);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(1));
        fixture.Analyzer.PauseNextCall();

        var submission = fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Answer", started.State.CurrentTurn!.TurnId));
        await fixture.Analyzer.WaitUntilPausedAsync();
        fixture.Clock.Set(StartTime.AddSeconds(40));
        fixture.Analyzer.Release();
        var result = await submission;

        Assert.Equal(StartTime.AddSeconds(40), result.Attempt.CurrentTurn!.AskedAt);
        Assert.Equal(StartTime.AddSeconds(50), result.Attempt.CurrentTurn.DeadlineAt);
    }

    [Fact]
    public async Task Slow_ai_processing_does_not_open_a_turn_after_the_exam_session_closes()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 10, questionCount: 2);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(1));
        fixture.Analyzer.PauseNextCall();

        var submission = fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Answer", started.State.CurrentTurn!.TurnId));
        await fixture.Analyzer.WaitUntilPausedAsync();
        fixture.Clock.Set(fixture.Session.EndTime.AddTicks(1));
        fixture.Analyzer.Release();
        var result = await submission;

        Assert.Equal(AttemptStatus.PendingReview.ToString(), result.Attempt.Status);
        Assert.Null(result.Attempt.CurrentTurn);
        Assert.Contains(fixture.Events.Events, interviewEvent => interviewEvent.Type == "attempt.expired");
        Assert.Single(fixture.Db.InterviewTurns);
    }

    [Fact]
    public async Task Concurrent_same_turn_retries_share_one_analysis_and_one_score()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        fixture.Analyzer.PauseNextCall();

        var originalTask = fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Same answer", turnId));
        await fixture.Analyzer.WaitUntilPausedAsync();
        var retry = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Same answer", turnId));
        fixture.Analyzer.Release();
        var original = await originalTask;

        Assert.False(original.Duplicate);
        Assert.True(retry.Duplicate);
        Assert.Equal(1, fixture.Analyzer.CallCount);
        Assert.Equal(1, fixture.Scorer.CallCount);
        Assert.Single(fixture.Db.QuestionResponses);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Single(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Submission_at_deadline_is_accepted_and_late_submission_is_rejected()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 10);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(10));

        var result = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("At the boundary", started.State.CurrentTurn!.TurnId));

        Assert.Equal(SubmitOutcome.Completed, result.Outcome);

        await using var lateFixture = await CreateFixtureAsync(timeLimitSeconds: 10);
        var lateStart = await lateFixture.Service.StartAttemptAsync(new StartAttemptRequest(lateFixture.Session.Id));
        lateFixture.Clock.Set(StartTime.AddSeconds(10).AddTicks(1));

        var error = await Assert.ThrowsAsync<ConflictException>(() => lateFixture.Service.SubmitAnswerAsync(
            lateStart.State.AttemptId, new SubmitAnswerRequest("Too late", lateStart.State.CurrentTurn!.TurnId)));

        Assert.Equal("answer_deadline_passed", error.Code);
        Assert.Null(lateFixture.Db.InterviewTurns.Single().AnswerTranscript);
        Assert.Null(lateFixture.Db.InterviewTurns.Single().AnsweredAt);
        Assert.Empty(lateFixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Starting_again_resumes_the_same_eligible_attempt_and_turn()
    {
        await using var fixture = await CreateFixtureAsync();
        var first = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));

        var resumed = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));

        Assert.True(first.Created);
        Assert.False(resumed.Created);
        Assert.Equal(first.State.AttemptId, resumed.State.AttemptId);
        Assert.Equal(first.State.CurrentTurn!.TurnId, resumed.State.CurrentTurn!.TurnId);
        Assert.Single(fixture.Db.InterviewAttempts);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Equal("attempt.resumed", fixture.Events.Events[^1].Type);
    }

    [Fact]
    public async Task Retake_obeys_configuration_and_creates_a_distinct_attempt()
    {
        await using var blocked = await CreateFixtureAsync(allowRetake: false);
        var first = await blocked.Service.StartAttemptAsync(new StartAttemptRequest(blocked.Session.Id));
        await blocked.Service.SubmitAnswerAsync(first.State.AttemptId,
            new SubmitAnswerRequest("First attempt", first.State.CurrentTurn!.TurnId));
        await Assert.ThrowsAsync<ConflictException>(() => blocked.Service.StartAttemptAsync(
            new StartAttemptRequest(blocked.Session.Id)));
        Assert.Single(blocked.Db.InterviewAttempts);

        await using var allowed = await CreateFixtureAsync(allowRetake: true);
        var firstAllowed = await allowed.Service.StartAttemptAsync(new StartAttemptRequest(allowed.Session.Id));
        await allowed.Service.SubmitAnswerAsync(firstAllowed.State.AttemptId,
            new SubmitAnswerRequest("First attempt", firstAllowed.State.CurrentTurn!.TurnId));

        var retake = await allowed.Service.StartAttemptAsync(new StartAttemptRequest(allowed.Session.Id));

        Assert.True(retake.Created);
        Assert.NotEqual(firstAllowed.State.AttemptId, retake.State.AttemptId);
        Assert.Equal(2, allowed.Db.InterviewAttempts.Count());
        Assert.Equal(2, allowed.Db.InterviewTurns.Count());
    }

    [Fact]
    public async Task Expiry_worker_processes_an_expired_turn_once()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 10);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(11));

        var processed = await fixture.Service.ExpireOverdueAttemptsAsync();
        var repeatedSweep = await fixture.Service.ExpireOverdueAttemptsAsync();

        Assert.Equal(1, processed);
        Assert.Equal(0, repeatedSweep);
        Assert.Equal(AttemptStatus.PendingReview, fixture.Db.InterviewAttempts.Single().Status);
        Assert.False(fixture.Db.InterviewTurns.Single().AnsweredAt.HasValue);
        Assert.True(fixture.Db.InterviewTurns.Single().IsTimedOut);
        Assert.Null(fixture.Db.InterviewTurns.Single().AnswerTranscript);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Equal(1, fixture.Scorer.CallCount);
        Assert.Null((await fixture.Service.GetAttemptStateAsync(started.State.AttemptId)).CurrentTurn);
        Assert.Contains(fixture.Events.Events, interviewEvent => interviewEvent.Type == "turn.deadline_reached");
        Assert.Contains(fixture.Events.Events, interviewEvent => interviewEvent.Type == "attempt.completed");
        var lateAnswer = await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Late retry", started.State.CurrentTurn!.TurnId)));
        Assert.Equal("answer_deadline_passed", lateAnswer.Code);
    }

    [Fact]
    public async Task Answer_and_expiry_sweep_race_keeps_one_consistent_turn_result()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 10);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(9));
        fixture.Analyzer.PauseNextCall();

        var submission = fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Accepted before deadline", started.State.CurrentTurn!.TurnId));
        await fixture.Analyzer.WaitUntilPausedAsync();
        fixture.Clock.Set(StartTime.AddSeconds(11));
        var expired = await fixture.Service.ExpireOverdueAttemptsAsync();
        fixture.Analyzer.Release();
        var result = await submission;

        Assert.Equal(0, expired);
        Assert.False(result.Duplicate);
        Assert.Equal(AttemptStatus.PendingReview, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Single(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Legacy_score_review_can_finalize_an_attempt_after_turn_processing()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Answer", started.State.CurrentTurn!.TurnId));
        var reviewerId = Guid.NewGuid();
        var review = new InterviewReviewService(fixture.Db, fixture.Configuration,
            new InterviewResultsApi(fixture.Db), fixture.Events, new TestUser(reviewerId, Roles.Lecturer), fixture.Clock);

        var approved = await review.SubmitScoreReviewAsync(started.State.AttemptId,
            new SubmitScoreReviewRequest(7m, "Confirmed"));

        Assert.Equal(ReviewStatus.Approved, approved.Status);
        Assert.Equal(AttemptStatus.Finalized, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal("review.finalized", fixture.Events.Events[^1].Type);
    }

    [Fact]
    public async Task Subscription_authorization_is_scoped_to_student_attempts_and_assigned_lecturers()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));

        Assert.Equal(fixture.Session.Id,
            await fixture.Service.AuthorizeAttemptSubscriptionAsync(started.State.AttemptId));
        await Assert.ThrowsAsync<NotFoundException>(() => fixture.CreateService(
                new TestUser(Guid.NewGuid(), Roles.Student))
            .AuthorizeAttemptSubscriptionAsync(started.State.AttemptId));
        Assert.Equal(fixture.Session.Id,
            await fixture.CreateService(new TestUser(Guid.NewGuid(), Roles.Lecturer))
                .AuthorizeAttemptSubscriptionAsync(started.State.AttemptId));
        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Service.AuthorizeSessionSubscriptionAsync(fixture.Session.Id));
    }

    [Fact]
    public async Task Get_attempt_state_recovers_current_authoritative_state_without_event_replay()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));

        var recovered = await fixture.Service.GetAttemptStateAsync(started.State.AttemptId);

        Assert.Equal(started.State.AttemptId, recovered.AttemptId);
        Assert.Equal(started.State.CurrentTurn?.TurnId, recovered.CurrentTurn?.TurnId);
        Assert.DoesNotContain("score", string.Join(',', typeof(AttemptStateDto).GetProperties().Select(property => property.Name)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Fixture> CreateFixtureAsync(
        int timeLimitSeconds = 60,
        bool allowRetake = false,
        int questionCount = 1,
        int maxFollowUps = 0)
    {
        var now = StartTime;
        var clock = new TestClock(now);
        var criterionId = Guid.NewGuid();
        var questions = Enumerable.Range(1, questionCount)
            .Select(orderNo => new QuestionInfo(Guid.NewGuid(), orderNo, $"Question {orderNo}",
                [new RubricCriterionInfo(criterionId, 1, "Accuracy", 10m)], 10m))
            .ToList();
        var session = new ExamSessionInfo(
            Guid.NewGuid(), Guid.NewGuid(), "TEST", "Test course", "Test exam",
            StartTime.AddHours(-1), StartTime.AddHours(1), timeLimitSeconds, maxFollowUps,
            ExamSessionStatus.Published,
            questions);
        var studentId = Guid.NewGuid();
        var db = new InterviewDbContext(new DbContextOptionsBuilder<InterviewDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Participants.Add(new Participant
        {
            Id = Guid.NewGuid(),
            ExamSessionId = session.Id,
            StudentId = studentId,
            JoinedAt = StartTime,
            Status = ParticipantStatus.Registered
        });
        await db.SaveChangesAsync();

        var analyzer = new TestAnalyzer();
        var scorer = new TestScorer(criterionId);
        var events = new TestEventPublisher(db);
        var configuration = new TestExamConfiguration(session);
        var service = new InterviewService(db, configuration, analyzer, scorer,
            events, new TestUser(studentId, Roles.Student), clock,
            Options.Create(new InterviewOptions { AllowRetake = allowRetake, TimeLimitGraceSeconds = 0 }),
            NullLogger<InterviewService>.Instance);
        return new Fixture(db, service, clock, analyzer, scorer, configuration, session, events);
    }

    private sealed class Fixture(
        InterviewDbContext db,
        InterviewService service,
        TestClock clock,
        TestAnalyzer analyzer,
        TestScorer scorer,
        TestExamConfiguration configuration,
        ExamSessionInfo session,
        TestEventPublisher events) : IAsyncDisposable
    {
        public InterviewDbContext Db { get; } = db;
        public InterviewService Service { get; } = service;
        public TestClock Clock { get; } = clock;
        public TestAnalyzer Analyzer { get; } = analyzer;
        public TestScorer Scorer { get; } = scorer;
        public TestExamConfiguration Configuration { get; } = configuration;
        public ExamSessionInfo Session { get; } = session;
        public TestEventPublisher Events { get; } = events;
        public InterviewService CreateService(TestUser user) => new(Db, Configuration, Analyzer, Scorer,
            Events, user, Clock, Options.Create(new InterviewOptions { TimeLimitGraceSeconds = 0 }),
            NullLogger<InterviewService>.Instance);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class TestEventPublisher(InterviewDbContext db) : IInterviewEventPublisher
    {
        public List<InterviewAttemptEvent> Events { get; } = [];

        public Task PublishAsync(InterviewAttemptEvent interviewEvent, CancellationToken ct = default)
        {
            var attempt = Assert.Single(db.InterviewAttempts.Where(item => item.Id == interviewEvent.AttemptId));
            if (interviewEvent.Type == "turn.opened")
                Assert.NotEmpty(db.InterviewTurns);
            if (interviewEvent.Type == "answer.accepted")
                Assert.Contains(db.InterviewTurns, turn => turn.AnsweredAt.HasValue);
            if (interviewEvent.Type is "turn.deadline_reached" or "turn.closed")
                Assert.Contains(db.InterviewTurns, turn => turn.IsTimedOut && turn.AnsweredAt is null);
            if (interviewEvent.Type == "review.finalized")
                Assert.Equal(AttemptStatus.Finalized, attempt.Status);
            Events.Add(interviewEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class TestClock(DateTime now) : TimeProvider
    {
        private DateTime _now = now;
        public override DateTimeOffset GetUtcNow() => new(_now);
        public void Set(DateTime value) => _now = value;
    }

    private sealed class TestUser(Guid id, string role) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => role;
        public bool IsInRole(string requestedRole) => role == requestedRole;
    }

    private sealed class TestAnalyzer : IAnswerAnalyzer
    {
        private TaskCompletionSource? _started;
        private TaskCompletionSource? _release;
        public int CallCount { get; private set; }
        public bool FailNext { get; set; }
        public bool NeedsFollowUp { get; set; }

        public async Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default)
        {
            CallCount++;
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Analyzer failure.");
            }
            if (_release is { } release)
            {
                _started!.TrySetResult();
                await release.Task.WaitAsync(ct);
                _release = null;
            }
            return new AnswerAnalysis(true, [], NeedsFollowUp, NeedsFollowUp ? "Follow-up question" : null);
        }

        public void PauseNextCall()
        {
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilPausedAsync() => _started!.Task;
        public void Release() => _release!.TrySetResult();
    }

    private sealed class TestScorer(Guid criterionId) : IRubricScorer
    {
        public int CallCount { get; private set; }
        public bool FailNext { get; set; }

        public Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default)
        {
            CallCount++;
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Scorer failure.");
            }
            return Task.FromResult(new RubricScore([new CriterionPoints(criterionId, 8m, "Good")], "Good answer."));
        }
    }

    private sealed class TestExamConfiguration(ExamSessionInfo session) : IExamConfigurationApi
    {
        public Task<ExamSessionInfo?> GetSessionAsync(Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult<ExamSessionInfo?>(examSessionId == session.Id ? session : null);

        public Task<IReadOnlyList<ExamSessionInfo>> GetSessionsAsync(
            IReadOnlyCollection<Guid> examSessionIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExamSessionInfo>>(examSessionIds.Contains(session.Id) ? [session] : []);

        public Task<bool> IsLecturerOfSessionAsync(Guid lecturerId, Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult(examSessionId == session.Id);

        public Task<IReadOnlyList<UserBasicInfo>> GetUsersAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserBasicInfo>>([]);

        public Task<UserAccessInfo?> GetUserAccessAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<UserAccessInfo?>(new UserAccessInfo(Roles.Lecturer, true));
    }
}
