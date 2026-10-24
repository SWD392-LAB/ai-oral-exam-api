namespace AiOralExam.Modules.AccessConfig.Contracts;

public sealed record RubricCriterionInfo(
    Guid Id,
    int OrderNo,
    string Description,
    decimal MaxPoints);
