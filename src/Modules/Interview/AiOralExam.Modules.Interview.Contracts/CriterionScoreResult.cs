namespace AiOralExam.Modules.Interview.Contracts;

public sealed record CriterionScoreResult(
    Guid RubricCriterionId,
    decimal? Points,
    string? Comment,
    string? Description = null,
    decimal? MaxPoints = null);
