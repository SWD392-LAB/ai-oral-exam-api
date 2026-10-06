namespace AiOralExam.Modules.Interview.Contracts;

/// <summary>
/// Reads the STORED results of attempts. Reporting uses this contract and
/// NEVER calls the AI again to re-score (business rule #5).
/// </summary>
public interface IInterviewResultsApi
{
    Task<AttemptResult?> GetAttemptResultAsync(Guid attemptId, CancellationToken ct = default);

    /// <summary>All finished attempts (PendingReview/Finalized) of an exam session.</summary>
    Task<IReadOnlyList<AttemptResult>> GetSessionResultsAsync(Guid examSessionId, CancellationToken ct = default);
}

public sealed record AttemptResult(
    Guid AttemptId,
    Guid ExamSessionId,
    Guid StudentId,
    AttemptStatus Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    ScoreReviewResult? Review,
    IReadOnlyList<QuestionResponseResult> Responses);

public sealed record ScoreReviewResult(
    Guid ReviewerId,
    decimal FinalScore,
    string? Comment,
    ReviewStatus Status,
    DateTime? ReviewedAt);

public sealed record QuestionResponseResult(
    Guid QuestionResponseId,
    Guid QuestionId,
    int FollowUpCount,
    bool IsFinished,
    decimal? SuggestedScore,
    string? AiFeedback,
    IReadOnlyList<TurnResult> Turns);

public sealed record TurnResult(
    int TurnNo,
    TurnType Type,
    string QuestionText,
    string? AnswerTranscript,
    DateTime AskedAt,
    DateTime? AnsweredAt);
