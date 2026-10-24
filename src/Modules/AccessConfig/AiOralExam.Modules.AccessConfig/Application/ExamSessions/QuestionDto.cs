using AiOralExam.Modules.AccessConfig.Contracts;

namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

public sealed record QuestionDto(
    Guid Id,
    int OrderNo,
    string Content,
    IReadOnlyList<RubricCriterionInfo> RubricCriteria,
    decimal MaxScore);
