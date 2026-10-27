using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Contracts;

namespace AiOralExam.Modules.Interview.Application.Features.ScoreReview;

public interface IInterviewReviewService
{
    Task<AttemptResult> GetAttemptReviewAsync(Guid attemptId, CancellationToken ct = default);

    Task<IReadOnlyList<AttemptResult>> GetSessionAttemptReviewsAsync(Guid examSessionId, CancellationToken ct = default);

    Task<ScoreReviewResult> SaveScoreReviewDraftAsync(
        Guid attemptId,
        SubmitScoreReviewRequest request,
        CancellationToken ct = default);

    Task<ScoreReviewResult> ConfirmScoreReviewAsync(Guid attemptId, CancellationToken ct = default);

    Task<ScoreReviewResult> SubmitScoreReviewAsync(
        Guid attemptId,
        SubmitScoreReviewRequest request,
        CancellationToken ct = default);
}
