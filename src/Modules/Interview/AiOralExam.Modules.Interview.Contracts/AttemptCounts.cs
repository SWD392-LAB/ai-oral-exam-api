namespace AiOralExam.Modules.Interview.Contracts;

public sealed record AttemptCounts(
    Guid ExamSessionId,
    int TakenCount,
    int ToReviewCount,
    int InProgressCount);
