using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Application;
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
    ICurrentUser currentUser,
    TimeProvider clock) : IInterviewReviewService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<AttemptResult> GetAttemptReviewAsync(Guid attemptId, CancellationToken ct = default)
    {
        var attempt = await LoadReviewAttemptAsync(attemptId, ct);
        if (attempt.Status == AttemptStatus.InProgress)
            throw new ConflictException("attempt_not_ready_for_review", "The attempt has not been completed.");
        if (attempt.Review is { Status: ReviewStatus.Pending } review && review.ReviewerId != currentUser.Id)
            throw new ForbiddenException("This review is assigned to another lecturer.");

        return await results.GetAttemptResultAsync(attemptId, ct)
               ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");
    }

    public async Task<ScoreReviewResult> SubmitScoreReviewAsync(
        Guid attemptId,
        SubmitScoreReviewRequest request,
        CancellationToken ct = default)
    {
        var attempt = await LoadReviewAttemptAsync(attemptId, ct);
        if (attempt.Status != AttemptStatus.PendingReview)
            throw new ConflictException("attempt_not_pending_review", "Only attempts pending review can be finalized.");
        if (attempt.Responses.Any(r => !r.IsFinished || r.AiEvaluationState != AiEvaluationState.Completed))
            throw new ConflictException("evaluation_pending", "All question responses must finish AI evaluation before review.");
        if (attempt.Review is { Status: not ReviewStatus.Pending })
            throw new ConflictException("score_review_already_submitted", "A score review has already been submitted.");
        if (attempt.Review is { } assignedReview && assignedReview.ReviewerId != currentUser.Id)
            throw new ForbiddenException("This review is assigned to another lecturer.");

        var session = await examConfig.GetSessionAsync(attempt.Participant.ExamSessionId, ct)
                      ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        ValidateScoreReviewRequest(request, session.TotalMaxScore);

        var now = UtcNow;
        var review = attempt.Review ?? new ScoreReviewEntity
        {
            Id = Guid.NewGuid(),
            AttemptId = attempt.Id,
            ReviewerId = currentUser.Id
        };
        review.FinalScore = request.FinalScore;
        review.Comment = request.Comment;
        review.Status = ReviewStatus.Approved;
        review.ReviewedAt = now;
        if (attempt.Review is null)
        {
            attempt.Review = review;
            db.ScoreReviews.Add(review);
        }

        attempt.TransitionTo(AttemptStatus.Finalized);
        await db.SaveChangesAsync(ct);

        return new(review.ReviewerId, review.FinalScore, review.Comment, review.Status, review.ReviewedAt);
    }

    private async Task<InterviewAttempt> LoadReviewAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        if (!currentUser.IsInRole(Roles.Lecturer))
            throw new ForbiddenException("Only lecturers can review attempts.");

        var attempt = await db.InterviewAttempts
                          .Include(a => a.Participant)
                          .Include(a => a.Review)
                          .Include(a => a.Responses)
                          .FirstOrDefaultAsync(a => a.Id == attemptId, ct)
                      ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");

        if (!await examConfig.IsLecturerOfSessionAsync(currentUser.Id, attempt.Participant.ExamSessionId, ct))
            throw new NotFoundException("attempt_not_found", "Attempt not found.");

        return attempt;
    }

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
