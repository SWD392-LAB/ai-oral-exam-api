namespace AiOralExam.Modules.AccessConfig.Contracts;

/// <summary>Criteria is empty while the question has no rubric yet (draft session).</summary>
public sealed record QuestionInfo(
    Guid Id,
    int OrderNo,
    string Content,
    IReadOnlyList<RubricCriterionInfo> Criteria,
    decimal MaxScore);
