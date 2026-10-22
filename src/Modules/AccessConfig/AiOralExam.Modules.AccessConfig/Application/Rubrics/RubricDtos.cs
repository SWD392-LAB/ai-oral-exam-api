namespace AiOralExam.Modules.AccessConfig.Application.Rubrics;

public sealed record RubricCriterionDto(Guid Id, int OrderNo, string Description, decimal MaxPoints);

public sealed record RubricDto(Guid Id, decimal MaxScore, IReadOnlyList<RubricCriterionDto> Criteria);
