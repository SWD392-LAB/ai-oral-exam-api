using System.Reflection;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Events;
using AiOralExam.Modules.Interview.Application.Features.ScoreReview;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using RangeAttribute = System.ComponentModel.DataAnnotations.RangeAttribute;

namespace AiOralExam.UnitTests;

public class InterviewScoreReviewTests
{
    [Fact]
    public void Score_review_range_validation_is_attached_to_the_constructor_parameter()
    {
        var requestType = typeof(SubmitScoreReviewRequest);
        var constructor = Assert.Single(requestType.GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters(),
            parameter => parameter.Name == nameof(SubmitScoreReviewRequest.FinalScore));
        var range = parameter.GetCustomAttribute<RangeAttribute>();

        Assert.NotNull(range);
        Assert.False(range!.IsValid(-1m));
        Assert.False(range.IsValid(1000m));
        Assert.True(range.IsValid(0m));
        Assert.True(range.IsValid(10m));
        Assert.Null(requestType.GetProperty(nameof(SubmitScoreReviewRequest.FinalScore))!
            .GetCustomAttribute<RangeAttribute>());
    }

    [Fact]
    public async Task Submit_score_review_approves_attempt_and_is_returned_by_get()
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var lecturer = new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer);
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.PendingReview);
        var service = CreateService(db, lecturer, new TestExamConfigurationApi(sessionId, assigned: true));

        var submitted = await service.SubmitScoreReviewAsync(
            attempt.Id, new SubmitScoreReviewRequest(8.5m, "Good work."));

        Assert.Equal(lecturer.Id, submitted.ReviewerId);
        Assert.Equal(8.5m, submitted.FinalScore);
        Assert.Equal("Good work.", submitted.Comment);
        Assert.Equal(ReviewStatus.Approved, submitted.Status);
        Assert.NotNull(submitted.ReviewedAt);
        Assert.Equal(AttemptStatus.Finalized, attempt.Status);
        var persisted = Assert.Single(db.ScoreReviews);
        Assert.Equal(lecturer.Id, persisted.ReviewerId);
        Assert.Equal(submitted.FinalScore, persisted.FinalScore);
        Assert.Equal(ReviewStatus.Approved, persisted.Status);

        var result = await service.GetAttemptReviewAsync(attempt.Id);
        Assert.Equal(attempt.Id, result.AttemptId);
        Assert.Equal(submitted, result.Review);
    }

    [Fact]
    public async Task Draft_remains_pending_until_confirmation_and_confirmation_is_idempotent()
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var lecturer = new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer);
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.PendingReview);
        var service = CreateService(db, lecturer, new TestExamConfigurationApi(sessionId, assigned: true));

        var draft = await service.SaveScoreReviewDraftAsync(attempt.Id, new SubmitScoreReviewRequest(8.5m, "Draft"));

        Assert.Equal(ReviewStatus.Pending, draft.Status);
        Assert.Null(draft.ReviewedAt);
        Assert.Equal(AttemptStatus.PendingReview, attempt.Status);
        Assert.Single(db.ScoreReviews);
        var details = await service.GetAttemptReviewAsync(attempt.Id);
        Assert.Equal(draft, details.Review);

        var confirmed = await service.ConfirmScoreReviewAsync(attempt.Id);
        var retried = await service.ConfirmScoreReviewAsync(attempt.Id);

        Assert.Equal(ReviewStatus.Approved, confirmed.Status);
        Assert.NotNull(confirmed.ReviewedAt);
        Assert.Equal(confirmed, retried);
        Assert.Equal(AttemptStatus.Finalized, attempt.Status);
        Assert.Single(db.ScoreReviews);
    }

    [Fact]
    public async Task Confirm_without_a_draft_is_rejected()
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.PendingReview);
        var service = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer),
            new TestExamConfigurationApi(sessionId, assigned: true));

        await Assert.ThrowsAsync<ConflictException>(() => service.ConfirmScoreReviewAsync(attempt.Id));
        Assert.Equal(AttemptStatus.PendingReview, attempt.Status);
        Assert.Empty(db.ScoreReviews);
    }

    [Fact]
    public async Task Session_listing_does_not_expose_another_lecturers_pending_draft()
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.PendingReview);
        var configuration = new TestExamConfigurationApi(sessionId, assigned: true);
        var firstLecturer = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer), configuration);
        var secondLecturer = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer), configuration);
        await firstLecturer.SaveScoreReviewDraftAsync(attempt.Id, new SubmitScoreReviewRequest(5m, "Private draft"));

        var visibleAttempts = await secondLecturer.GetSessionAttemptReviewsAsync(sessionId);

        Assert.Empty(visibleAttempts);
    }

    [Fact]
    public async Task Review_details_include_pinned_criterion_and_reject_out_of_range_ai_scores()
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var criterionId = Guid.NewGuid();
        var criteria = new[] { new RubricCriterionInfo(criterionId, 1, "Accuracy", 5m) };
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.PendingReview);
        var configuration = new TestExamConfigurationApi(sessionId, assigned: true, criteria);
        var response = new QuestionResponse
        {
            Id = Guid.NewGuid(),
            AttemptId = attempt.Id,
            QuestionId = configuration.QuestionId,
            IsFinished = true,
            AiSuggestedScore = 5m,
            AiFeedback = "Good",
            CriterionScores =
            {
                new CriterionScore
                {
                    Id = Guid.NewGuid(),
                    RubricCriterionId = criterionId,
                    Points = 5m
                }
            }
        };
        attempt.Responses.Add(response);
        db.QuestionResponses.Add(response);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer), configuration);

        var result = await service.GetAttemptReviewAsync(attempt.Id);

        var criterionScore = Assert.Single(Assert.Single(result.Responses).CriterionScores!);
        Assert.Equal("Accuracy", criterionScore.Description);
        Assert.Equal(5m, criterionScore.MaxPoints);
        Assert.Equal(5m, criterionScore.Points);

        response.CriterionScores.Single().Points = 5.01m;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => service.GetAttemptReviewAsync(attempt.Id));
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(10.01d)]
    public async Task Submit_score_review_rejects_scores_outside_valid_range(double finalScore)
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.PendingReview);
        var service = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer),
            new TestExamConfigurationApi(sessionId, assigned: true));

        await Assert.ThrowsAsync<ValidationException>(() => service.SubmitScoreReviewAsync(
            attempt.Id, new SubmitScoreReviewRequest((decimal)finalScore, null)));

        Assert.Equal(AttemptStatus.PendingReview, attempt.Status);
        Assert.Empty(db.ScoreReviews);
    }

    [Fact]
    public async Task Submit_score_review_rejects_attempt_that_is_still_in_progress()
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.InProgress);
        var service = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer),
            new TestExamConfigurationApi(sessionId, assigned: true));

        await Assert.ThrowsAsync<ConflictException>(() => service.SubmitScoreReviewAsync(
            attempt.Id, new SubmitScoreReviewRequest(5m, null)));

        Assert.Equal(AttemptStatus.InProgress, attempt.Status);
        Assert.Empty(db.ScoreReviews);
    }

    [Fact]
    public async Task Review_endpoints_report_a_missing_attempt()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer),
            new TestExamConfigurationApi(Guid.NewGuid(), assigned: true));
        var missingId = Guid.NewGuid();

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAttemptReviewAsync(missingId));
        await Assert.ThrowsAsync<NotFoundException>(() => service.SubmitScoreReviewAsync(
            missingId, new SubmitScoreReviewRequest(5m, null)));
    }

    [Fact]
    public async Task Lecturer_cannot_review_attempt_for_a_course_they_are_not_assigned_to()
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.PendingReview);
        var service = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer),
            new TestExamConfigurationApi(sessionId, assigned: false));

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAttemptReviewAsync(attempt.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetSessionAttemptReviewsAsync(sessionId));
        await Assert.ThrowsAsync<NotFoundException>(() => service.SubmitScoreReviewAsync(
            attempt.Id, new SubmitScoreReviewRequest(5m, null)));

        Assert.Equal(AttemptStatus.PendingReview, attempt.Status);
        Assert.Empty(db.ScoreReviews);
    }

    [Fact]
    public async Task Non_lecturer_cannot_review_attempt_even_when_assigned_to_course()
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.PendingReview);
        var service = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Student),
            new TestExamConfigurationApi(sessionId, assigned: true));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.SubmitScoreReviewAsync(
            attempt.Id, new SubmitScoreReviewRequest(5m, null)));
    }

    [Fact]
    public async Task Submit_score_review_returns_the_stored_result_for_an_identical_finalized_retry()
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.PendingReview);
        var service = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer),
            new TestExamConfigurationApi(sessionId, assigned: true));
        var request = new SubmitScoreReviewRequest(5m, null);

        var submitted = await service.SubmitScoreReviewAsync(attempt.Id, request);

        var retry = await service.SubmitScoreReviewAsync(attempt.Id, request);

        Assert.Equal(submitted, retry);
        Assert.Single(db.ScoreReviews);
    }

    [Fact]
    public async Task Finalized_review_rejects_a_different_reviewer_or_changed_payload()
    {
        await using var db = CreateDbContext();
        var sessionId = Guid.NewGuid();
        var attempt = await AddAttemptAsync(db, sessionId, AttemptStatus.PendingReview);
        var configuration = new TestExamConfigurationApi(sessionId, assigned: true);
        var firstReviewer = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer), configuration);
        var secondReviewer = CreateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer), configuration);
        var request = new SubmitScoreReviewRequest(5m, "Approved");
        await firstReviewer.SubmitScoreReviewAsync(attempt.Id, request);

        await Assert.ThrowsAsync<ConflictException>(() => firstReviewer.SubmitScoreReviewAsync(attempt.Id,
            new SubmitScoreReviewRequest(6m, "Approved")));
        await Assert.ThrowsAsync<ConflictException>(() => secondReviewer.SubmitScoreReviewAsync(attempt.Id, request));
        Assert.Single(db.ScoreReviews);
        Assert.Equal(5m, (await db.ScoreReviews.SingleAsync()).FinalScore);
    }

    private static InterviewReviewService CreateService(
        InterviewDbContext db,
        TestCurrentUser currentUser,
        IExamConfigurationApi examConfig) => new(
        db,
        examConfig,
        new InterviewResultsApi(db),
        new NoOpInterviewEventPublisher(),
        currentUser,
        TimeProvider.System);

    private sealed class NoOpInterviewEventPublisher : IInterviewEventPublisher
    {
        public Task PublishAsync(InterviewAttemptEvent interviewEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task<InterviewAttempt> AddAttemptAsync(
        InterviewDbContext db,
        Guid sessionId,
        AttemptStatus status)
    {
        var participant = new Participant
        {
            Id = Guid.NewGuid(),
            ExamSessionId = sessionId,
            StudentId = Guid.NewGuid(),
            JoinedAt = DateTime.UtcNow,
            Status = status == AttemptStatus.InProgress ? ParticipantStatus.InProgress : ParticipantStatus.Completed
        };
        var attempt = new InterviewAttempt
        {
            Id = Guid.NewGuid(),
            ParticipantId = participant.Id,
            Participant = participant,
            StartedAt = DateTime.UtcNow
        };
        if (status != AttemptStatus.InProgress)
            attempt.Complete(DateTime.UtcNow);
        if (status == AttemptStatus.Finalized)
            attempt.TransitionTo(AttemptStatus.Finalized);
        participant.Attempts.Add(attempt);
        db.Participants.Add(participant);
        await db.SaveChangesAsync();
        return attempt;
    }

    private static InterviewDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InterviewDbContext(options);
    }

    private sealed class TestCurrentUser(Guid id, string role) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => role;
        public bool IsInRole(string requestedRole) => role == requestedRole;
    }

    private sealed class TestExamConfigurationApi(
        Guid sessionId,
        bool assigned,
        IReadOnlyList<RubricCriterionInfo>? criteria = null) : IExamConfigurationApi
    {
        public Guid QuestionId => _session.Questions[0].Id;

        private readonly ExamSessionInfo _session = CreateSession(sessionId, criteria);

        private static ExamSessionInfo CreateSession(Guid sessionId, IReadOnlyList<RubricCriterionInfo>? criteria)
        {
            var questionId = Guid.NewGuid();
            return new ExamSessionInfo(
                sessionId, Guid.NewGuid(), "TEST", "Test Course", "Test Session",
                DateTime.UtcNow, DateTime.UtcNow.AddHours(1), 60, 2, ExamSessionStatus.Published,
                [new QuestionInfo(questionId, 1, "Question", criteria ?? [], 10m)]);
        }

        public Task<ExamSessionInfo?> GetSessionAsync(Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult<ExamSessionInfo?>(examSessionId == _session.Id ? _session : null);

        public Task<IReadOnlyList<ExamSessionInfo>> GetSessionsAsync(
            IReadOnlyCollection<Guid> examSessionIds, CancellationToken ct = default)
        {
            IReadOnlyList<ExamSessionInfo> sessions = examSessionIds.Contains(_session.Id)
                ? new[] { _session }
                : Array.Empty<ExamSessionInfo>();
            return Task.FromResult(sessions);
        }

        public Task<bool> IsLecturerOfSessionAsync(Guid lecturerId, Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult(assigned && examSessionId == _session.Id);

        public Task<IReadOnlyList<UserBasicInfo>> GetUsersAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserBasicInfo>>(Array.Empty<UserBasicInfo>());

        public Task<UserAccessInfo?> GetUserAccessAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<UserAccessInfo?>(new UserAccessInfo(Roles.Lecturer, true));
    }

}
