using AiOralExam.Modules.AccessConfig.Contracts;

namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

public sealed record ExamSessionSummaryDto(
    Guid Id,
    string CourseCode,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    int TimeLimitPerAnswerSeconds,
    int MaxFollowUps,
    string Status,
    int QuestionCount,
    int ParticipantCount,
    int TakenCount,
    int ToReviewCount);

public sealed record QuestionDto(
    Guid Id,
    int OrderNo,
    string Content,
    IReadOnlyList<RubricCriterionInfo> RubricCriteria,
    decimal MaxScore);

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
    IReadOnlyList<QuestionDto> Questions);

public sealed record CourseDto(Guid Id, string Code, string Name);
