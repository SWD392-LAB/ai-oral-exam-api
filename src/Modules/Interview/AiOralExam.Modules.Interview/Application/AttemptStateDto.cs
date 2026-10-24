namespace AiOralExam.Modules.Interview.Application;

public sealed record AttemptStateDto(
    Guid AttemptId,
    Guid ExamSessionId,
    string ExamTitle,
    string Status,
    int TimeLimitSeconds,
    int MaxFollowUps,
    int TotalQuestions,
    int AnsweredQuestions,
    DateTime StartedAt,
    DateTime? CompletedAt,
    CurrentTurnDto? CurrentTurn);
