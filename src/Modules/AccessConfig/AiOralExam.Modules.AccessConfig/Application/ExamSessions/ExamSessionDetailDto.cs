namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

public sealed record ExamSessionDetailDto(
    Guid Id,
    Guid CourseId,
    string CourseCode,
    string CourseName,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    int TimeLimitPerAnswerSeconds,
    int MaxFollowUps,
    string Status,
    IReadOnlyList<QuestionDto> Questions,
    string RubricMode = "Legacy",
    Guid? RubricVersionId = null);
