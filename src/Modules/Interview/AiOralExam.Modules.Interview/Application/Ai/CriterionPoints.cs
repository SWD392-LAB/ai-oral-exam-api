namespace AiOralExam.Modules.Interview.Application.Ai;

/// <summary>Points the AI gives one rubric criterion (0..MaxPoints).</summary>
public sealed record CriterionPoints(Guid CriterionId, decimal? Points, string? Comment);
