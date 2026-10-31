using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Events;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using ScoreReviewEntity = AiOralExam.Modules.Interview.Domain.ScoreReview;

namespace AiOralExam.Modules.Interview.Application.Features.ScoreReview;

internal sealed class InterviewReviewService(
    InterviewDbContext db,
    IExamConfigurationApi examConfig,
    IInterviewResultsApi results,
    IInterviewEventPublisher eventPublisher,
    ICurrentUser currentUser,
    TimeProvider clock) : IInterviewReviewService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<AttemptResult> GetAttemptReviewAsync(Guid attemptId, CancellationToken ct = default)
    {
        var attempt = await LoadReviewAttemptAsync(attemptId, ct);
        if (attempt.Status == AttemptStatus.InProgress)
            throw new ConflictException("attempt_not_ready_for_review", "The attempt has not been completed.");
        if (attempt.Status == AttemptStatus.Interrupted)
            throw new ConflictException("attempt_not_ready_for_review", "The attempt was interrupted before AI evaluation completed.");
        if (attempt.Review is { Status: ReviewStatus.Pending } review && review.ReviewerId != currentUser.Id)
            throw new ForbiddenException("This review is assigned to another lecturer.");

        var session = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
        var result = await results.GetAttemptResultAsync(attemptId, ct)
                     ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");
        return AddPinnedRubricDetailsAndValidate(result, session);
    }

    public async Task<IReadOnlyList<AttemptResult>> GetSessionAttemptReviewsAsync(
        Guid examSessionId,
        CancellationToken ct = default)
    {
        EnsureLecturer();
        if (!await examConfig.IsLecturerOfSessionAsync(currentUser.Id, examSessionId, ct))
            throw new NotFoundException("exam_session_not_found", "Exam session not found.");

        var session = await GetSessionAsync(examSessionId, ct);
        var attempts = await results.GetSessionResultsAsync(examSessionId, ct);
        return attempts
            .Where(a => a.Review is { Status: ReviewStatus.Pending, ReviewerId: var reviewerId }
                ? reviewerId == currentUser.Id
                : true)
            .Select(a => AddPinnedRubricDetailsAndValidate(a, session))
            .ToList();
    }

    public Task<ScoreReviewResult> SaveScoreReviewDraftAsync(
        Guid attemptId,
        SubmitScoreReviewRequest request,
        CancellationToken ct = default) =>
        WithAttemptLockAsync(attemptId, async () =>
        {
            var attempt = await LoadReviewAttemptAsync(attemptId, ct);
            if (attempt.Status == AttemptStatus.Interrupted)
            {
                if (attempt.Review is { } interruptedReview && interruptedReview.ReviewerId != currentUser.Id)
                    throw new ForbiddenException("This review is assigned to another lecturer.");
                EnsureEvaluationComplete(attempt,
                    await GetSessionAsync(attempt.Participant.ExamSessionId, ct));
            }
            if (attempt.Status != AttemptStatus.PendingReview)
                throw new ConflictException("attempt_not_pending_review", "Only attempts pending review can be edited.");
            if (attempt.Review is { Status: not ReviewStatus.Pending })
                throw new ConflictException("score_review_already_submitted", "A score review has already been submitted.");
            if (attempt.Review is { } existing && existing.ReviewerId != currentUser.Id)
                throw new ForbiddenException("This review is assigned to another lecturer.");

            var session = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
            EnsureEvaluationComplete(attempt, session);
            ValidateScoreReviewRequest(request, session.TotalMaxScore);
            await ValidateAttemptScoresAsync(attemptId, session, ct);

            var review = attempt.Review ?? CreateReview(attempt);
            review.FinalScore = request.FinalScore;
            review.Comment = request.Comment;
            review.Status = ReviewStatus.Pending;
            review.ReviewedAt = null;
            if (attempt.Review is null)
            {
                attempt.Review = review;
                db.ScoreReviews.Add(review);
            }
            return ToResult(review);
        }, ct);

    public async Task<ScoreReviewResult> ConfirmScoreReviewAsync(Guid attemptId, CancellationToken ct = default)
    {
        InterviewAttemptEvent? interviewEvent = null;
        var result = await WithAttemptLockAsync(attemptId, async () =>
        {
            var attempt = await LoadReviewAttemptAsync(attemptId, ct);
            if (attempt.Review is { Status: ReviewStatus.Approved } approved)
            {
                if (approved.ReviewerId != currentUser.Id)
                    throw new ForbiddenException("This review is assigned to another lecturer.");
                return ToResult(approved);
            }
            if (attempt.Review is { Status: ReviewStatus.Pending } pending
                && pending.ReviewerId != currentUser.Id)
                throw new ForbiddenException("This review is assigned to another lecturer.");
            var session = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
            EnsureEvaluationComplete(attempt, session);
            if (attempt.Status != AttemptStatus.PendingReview || attempt.Review is not { Status: ReviewStatus.Pending } review)
                throw new ConflictException("score_review_draft_not_found", "Save a review draft before confirming it.");
            if (review.ReviewerId != currentUser.Id)
                throw new ForbiddenException("This review is assigned to another lecturer.");

            ValidateScoreReviewRequest(new SubmitScoreReviewRequest(review.FinalScore, review.Comment), session.TotalMaxScore);
            await ValidateAttemptScoresAsync(attemptId, session, ct);
            ApproveReview(attempt, review);
            interviewEvent = CreateFinalizedEvent(attempt, session.Id);
            return ToResult(review);
        }, ct);

        if (interviewEvent is not null)
            await eventPublisher.PublishAsync(interviewEvent, ct);
        return result;
    }

    public async Task<ScoreReviewResult> SubmitScoreReviewAsync(
        Guid attemptId,
        SubmitScoreReviewRequest request,
        CancellationToken ct = default)
    {
        InterviewAttemptEvent? interviewEvent = null;
        var result = await WithAttemptLockAsync(attemptId, async () =>
        {
            var attempt = await LoadReviewAttemptAsync(attemptId, ct);
            if (attempt.Status == AttemptStatus.Finalized && attempt.Review is { Status: ReviewStatus.Approved } finalized)
            {
                if (finalized.ReviewerId == currentUser.Id
                    && finalized.FinalScore == request.FinalScore
                    && string.Equals(finalized.Comment, request.Comment, StringComparison.Ordinal))
                    return ToResult(finalized);

                throw new ConflictException("score_review_already_submitted",
                    "The finalized score review cannot be changed or submitted by another reviewer.");
            }
            if (attempt.Review is { } existing && existing.ReviewerId != currentUser.Id)
                throw new ForbiddenException("This review is assigned to another lecturer.");
            var session = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
            EnsureEvaluationComplete(attempt, session);
            if (attempt.Status != AttemptStatus.PendingReview)
                throw new ConflictException("attempt_not_pending_review", "Only attempts pending review can be finalized.");
            if (attempt.Review is { Status: not ReviewStatus.Pending })
                throw new ConflictException("score_review_already_submitted", "A score review has already been submitted.");
            ValidateScoreReviewRequest(request, session.TotalMaxScore);
            await ValidateAttemptScoresAsync(attemptId, session, ct);

            var review = attempt.Review ?? CreateReview(attempt);
            review.FinalScore = request.FinalScore;
            review.Comment = request.Comment;
            ApproveReview(attempt, review);
            interviewEvent = CreateFinalizedEvent(attempt, session.Id);
            if (attempt.Review is null)
            {
                attempt.Review = review;
                db.ScoreReviews.Add(review);
            }
            return ToResult(review);
        }, ct);

        if (interviewEvent is not null)
            await eventPublisher.PublishAsync(interviewEvent, ct);
        return result;
    }

    private async Task<T> WithAttemptLockAsync<T>(Guid attemptId, Func<Task<T>> operation, CancellationToken ct)
    {
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM interview_attempts WHERE id = {attemptId} FOR UPDATE", ct);
            var result = await operation();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }

        var inMemoryResult = await operation();
        await db.SaveChangesAsync(ct);
        return inMemoryResult;
    }

    private async Task<InterviewAttempt> LoadReviewAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        EnsureLecturer();

        var attempt = await db.InterviewAttempts
                          .Include(a => a.Participant).ThenInclude(p => p.Attempts)
                          .Include(a => a.Review)
                          .Include(a => a.Responses)
                          .FirstOrDefaultAsync(a => a.Id == attemptId, ct)
                      ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");

        if (!await examConfig.IsLecturerOfSessionAsync(currentUser.Id, attempt.Participant.ExamSessionId, ct))
            throw new NotFoundException("attempt_not_found", "Attempt not found.");

        return attempt;
    }

    private static void EnsureEvaluationComplete(InterviewAttempt attempt, ExamSessionInfo session)
    {
        var expectedQuestionIds = session.Questions.Select(question => question.Id).ToHashSet();
        if (attempt.Status is AttemptStatus.InProgress or AttemptStatus.Interrupted
            || expectedQuestionIds.Count == 0
            || attempt.Responses.Count != expectedQuestionIds.Count
            || !attempt.Responses.Select(response => response.QuestionId).ToHashSet().SetEquals(expectedQuestionIds)
            || attempt.Responses.Any(response => !response.IsFinished
            || response.AiSuggestedScore is null
            || response.EvaluatedAt is null
            || response.Turns.Count == 0
            || response.Turns.Any(turn => turn.ProcessingStatus != TurnProcessingStatus.Completed
                || turn.AnsweredAt is null && !turn.IsTimedOut)))
            throw new ConflictException("evaluation_pending", "AI evaluation is still in progress.");
    }

    private async Task<ExamSessionInfo> GetSessionAsync(Guid sessionId, CancellationToken ct) =>
        await examConfig.GetSessionAsync(sessionId, ct)
        ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");

    private async Task ValidateAttemptScoresAsync(Guid attemptId, ExamSessionInfo session, CancellationToken ct)
    {
        var result = await results.GetAttemptResultAsync(attemptId, ct)
                     ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");
        _ = AddPinnedRubricDetailsAndValidate(result, session);
    }

    private static AttemptResult AddPinnedRubricDetailsAndValidate(AttemptResult result, ExamSessionInfo session)
    {
        var questions = session.Questions.ToDictionary(q => q.Id);
        var responses = result.Responses.Select(response =>
        {
            if (!questions.TryGetValue(response.QuestionId, out var question))
                throw InvalidCriterionScores();

            var scores = response.CriterionScores ?? [];
            if (response.SuggestedScore is null)
            {
                if (scores.Count > 0)
                    throw InvalidCriterionScores();
                return response with { CriterionScores = question.Criteria
                    .Select(c => new CriterionScoreResult(c.Id, null, null, c.Description, c.MaxPoints)).ToList() };
            }

            if (scores.Select(s => s.RubricCriterionId).Distinct().Count() != scores.Count)
                throw InvalidCriterionScores();
            var byCriterion = scores.ToDictionary(s => s.RubricCriterionId);
            if (scores.Count != question.Criteria.Count || byCriterion.Count != scores.Count)
                throw InvalidCriterionScores();

            var details = question.Criteria.Select(criterion =>
            {
                if (!byCriterion.TryGetValue(criterion.Id, out var score)
                    || score.Points is not decimal points
                    || points < 0 || points > criterion.MaxPoints
                    || points != decimal.Round(points, 2))
                    throw InvalidCriterionScores();
                return score with { Description = criterion.Description, MaxPoints = criterion.MaxPoints };
            }).ToList();
            if (details.Sum(s => s.Points!.Value) != response.SuggestedScore)
                throw InvalidCriterionScores();
            return response with { CriterionScores = details };
        }).ToList();

        return result with { Responses = responses };
    }

    private static ConflictException InvalidCriterionScores() =>
        new("attempt_rubric_scores_invalid", "Stored AI criterion scores do not match the session's pinned rubric.");

    private void EnsureLecturer()
    {
        if (!currentUser.IsInRole(Roles.Lecturer))
            throw new ForbiddenException("Only lecturers can review attempts.");
    }

    private ScoreReviewEntity CreateReview(InterviewAttempt attempt) => new()
    {
        Id = Guid.NewGuid(),
        AttemptId = attempt.Id,
        ReviewerId = currentUser.Id
    };

    private void ApproveReview(InterviewAttempt attempt, ScoreReviewEntity review)
    {
        review.Status = ReviewStatus.Approved;
        review.ReviewedAt = UtcNow;
        attempt.TransitionTo(AttemptStatus.Finalized);
        attempt.Participant.ReconcileStatus();
    }

    private InterviewAttemptEvent CreateFinalizedEvent(InterviewAttempt attempt, Guid sessionId)
    {
        var turnCount = attempt.Responses.Sum(response => response.Turns.Count);
        var sequence = InterviewEventSequence.AttemptCompleted(turnCount) + 1;
        var turnId = attempt.Responses.SelectMany(response => response.Turns)
            .MaxBy(turn => turn.AskedAt)?.Id;
        return new InterviewAttemptEvent(Guid.NewGuid(), attempt.Id, sessionId,
            sequence, turnId, sequence, "review.finalized", UtcNow);
    }

    private static ScoreReviewResult ToResult(ScoreReviewEntity review) =>
        new(review.ReviewerId, review.FinalScore, review.Comment, review.Status, review.ReviewedAt);

    private static void ValidateScoreReviewRequest(SubmitScoreReviewRequest request, decimal sessionMaxScore)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.FinalScore < 0 || request.FinalScore > 999.99m || request.FinalScore > sessionMaxScore)
            errors[nameof(request.FinalScore)] = ["Final score must be non-negative and no greater than the session maximum."];
        else if (request.FinalScore != decimal.Round(request.FinalScore, 2))
            errors[nameof(request.FinalScore)] = ["Final score must have no more than two decimal places."];

        if (errors.Count > 0)
            throw new ValidationException(errors);
    }
}
