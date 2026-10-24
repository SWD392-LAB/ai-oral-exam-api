namespace AiOralExam.Modules.AccessConfig.Contracts;

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
