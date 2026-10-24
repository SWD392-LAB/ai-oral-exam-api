namespace AiOralExam.Modules.Interview.Application;

public sealed record MySessionDto(
    Guid ExamSessionId,
    string CourseCode,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    string SessionStatus,
    bool IsOpenNow,
    int QuestionCount,
    int TimeLimitPerAnswerSeconds,
    string ParticipantStatus,
    Guid? LatestAttemptId,
    string? LatestAttemptStatus);
