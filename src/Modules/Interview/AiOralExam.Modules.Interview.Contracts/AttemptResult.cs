namespace AiOralExam.Modules.Interview.Contracts;

public sealed record AttemptResult(
    Guid AttemptId,
    Guid ExamSessionId,
    Guid StudentId,
    AttemptStatus Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    ScoreReviewResult? Review,
    IReadOnlyList<QuestionResponseResult> Responses);
