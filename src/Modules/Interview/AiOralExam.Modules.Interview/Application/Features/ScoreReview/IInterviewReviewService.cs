using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Contracts;

namespace AiOralExam.Modules.Interview.Application.Features.ScoreReview;

public interface IInterviewReviewService
{
    Task<AttemptResult> GetAttemptReviewAsync(Guid attemptId, CancellationToken ct = default);

    Task<ScoreReviewResult> SubmitScoreReviewAsync(
        Guid attemptId,
        SubmitScoreReviewRequest request,
        CancellationToken ct = default);
}
