namespace AiOralExam.Modules.AccessConfig.Contracts;

public sealed record RubricCriterionInfo(
    Guid Id,
    int OrderNo,
    string Description,
    decimal MaxPoints);

/// <summary>Criteria is empty while the question has no rubric yet (draft session).</summary>
public sealed record QuestionInfo(
    Guid Id,
    int OrderNo,
    string Content,
    IReadOnlyList<RubricCriterionInfo> Criteria,
    decimal MaxScore);

public sealed record ExamSessionInfo(
    Guid Id,
    Guid CourseId,
    string CourseCode,
    string CourseName,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    int TimeLimitPerAnswerSeconds,
    int MaxFollowUps,
    ExamSessionStatus Status,
    IReadOnlyList<QuestionInfo> Questions)
{
    public bool IsOpenAt(DateTime utcNow) =>
        Status == ExamSessionStatus.Published && utcNow >= StartTime && utcNow < EndTime;

    public decimal TotalMaxScore => Questions.Sum(q => q.MaxScore);
}

public sealed record UserAccessInfo(string Role, bool IsActive);

public sealed record UserBasicInfo(
    Guid Id,
    string FullName,
    string Email,
    string? StudentCode);
