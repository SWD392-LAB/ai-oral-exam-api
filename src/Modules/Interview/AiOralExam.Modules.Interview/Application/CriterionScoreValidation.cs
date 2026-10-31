using AiOralExam.Modules.AccessConfig.Contracts;

namespace AiOralExam.Modules.Interview.Application;

internal static class CriterionScoreValidation
{
    public static CriterionScoreValidationResult Validate(
        IReadOnlyList<RubricCriterionInfo> criteria,
        IEnumerable<(Guid CriterionId, decimal? Points)> scores)
    {
        var scoreList = scores.ToList();
        if (scoreList.Count != criteria.Count
            || scoreList.Select(score => score.CriterionId).Distinct().Count() != scoreList.Count)
            return CriterionScoreValidationResult.IncompleteOrDuplicate;

        var byCriterion = scoreList.ToDictionary(score => score.CriterionId);
        return criteria.Any(criterion => !byCriterion.TryGetValue(criterion.Id, out var score)
            || score.Points is not decimal points
            || points < 0
            || points > criterion.MaxPoints
            || points != decimal.Round(points, 2))
            ? CriterionScoreValidationResult.Invalid
            : CriterionScoreValidationResult.Valid;
    }
}

internal enum CriterionScoreValidationResult
{
    Valid,
    IncompleteOrDuplicate,
    Invalid
}
