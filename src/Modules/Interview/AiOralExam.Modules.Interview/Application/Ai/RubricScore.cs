namespace AiOralExam.Modules.Interview.Application.Ai;

/// <summary>One CriterionPoints per criterion; the suggested score is their sum.</summary>
public sealed record RubricScore(IReadOnlyList<CriterionPoints> Scores, string Feedback);
