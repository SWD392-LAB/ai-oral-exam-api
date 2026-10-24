namespace AiOralExam.Modules.AccessConfig.Application.Rubrics;

public sealed record RubricDto(Guid Id, decimal MaxScore, IReadOnlyList<RubricCriterionDto> Criteria);
