namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

public sealed record ExamSessionCommandDto(
    Guid Id,
    Guid CourseId,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    int TimeLimitPerAnswerSeconds,
    int MaxFollowUps,
    string Status,
    int QuestionCount,
    int ParticipantCount,
    string RubricMode = "Legacy",
    Guid? RubricVersionId = null);
