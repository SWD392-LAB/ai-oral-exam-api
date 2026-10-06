namespace AiOralExam.Modules.Interview.Contracts;

/// <summary>
/// Doc ket qua da LUU cua luot thi. Reporting dung contract nay va
/// KHONG BAO GIO goi lai AI de cham lai (quy tac nghiep vu #5).
/// </summary>
public interface IInterviewResultsApi
{
    Task<AttemptResult?> GetAttemptResultAsync(Guid attemptId, CancellationToken ct = default);

    /// <summary>Moi luot thi da ket thuc (PendingReview/Finalized) cua mot phien thi.</summary>
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
