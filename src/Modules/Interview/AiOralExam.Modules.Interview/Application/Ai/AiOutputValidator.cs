namespace AiOralExam.Modules.Interview.Application.Ai;

internal static class AiOutputValidator
{
    public static AnswerAnalysis Validate(AnswerAnalysisRequest request, AnswerAnalysis output)
    {
        if (output.MissingPoints is null || output.MissingPoints.Any(string.IsNullOrWhiteSpace))
            throw Invalid("Analyzer returned an invalid missing-points list.");

        if (output.NeedsFollowUp && request.FollowUpsUsed >= request.MaxFollowUps)
            throw Invalid("Analyzer requested a follow-up after the quota was exhausted.");

        if (output.NeedsFollowUp && string.IsNullOrWhiteSpace(output.FollowUpQuestion))
            throw Invalid("Analyzer requested a follow-up without a question.");

        if (!output.NeedsFollowUp && output.FollowUpQuestion is not null)
            throw Invalid("Analyzer returned a follow-up question without requesting one.");

        return output with
        {
            MissingPoints = output.MissingPoints.Select(x => x.Trim()).ToArray(),
            FollowUpQuestion = output.FollowUpQuestion?.Trim()
        };
    }

    public static RubricScore Validate(RubricScoringRequest request, RubricScore output)
    {
        if (request.Criteria.Count == 0)
            throw Invalid("The question has no rubric criteria.");

        if (string.IsNullOrWhiteSpace(output.Feedback))
            throw Invalid("Scorer returned empty feedback.");

        var duplicateCriterion = request.Criteria.GroupBy(x => x.Id).FirstOrDefault(x => x.Count() > 1);
        if (duplicateCriterion is not null)
            throw Invalid($"The request contains rubric criterion {duplicateCriterion.Key} more than once.");

        var criteria = request.Criteria.ToDictionary(x => x.Id);
        var scores = output.Scores ?? throw Invalid("Scorer returned no criterion scores.");
        if (scores.Count != criteria.Count)
            throw Invalid("Scorer must return exactly one score for every criterion.");

        var duplicate = scores.GroupBy(x => x.CriterionId).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw Invalid($"Scorer returned criterion {duplicate.Key} more than once.");

        foreach (var score in scores)
        {
            if (!criteria.TryGetValue(score.CriterionId, out var criterion))
                throw Invalid($"Scorer returned unknown criterion {score.CriterionId}.");
            if (score.Points < 0 || score.Points > criterion.MaxPoints)
                throw Invalid($"Score for criterion {score.CriterionId} is outside its allowed range.");
        }

        return output with
        {
            Scores = scores.Select(x => x with { Comment = x.Comment?.Trim() }).ToArray(),
            Feedback = output.Feedback.Trim()
        };
    }

    private static AiEvaluationException Invalid(string detail) => new(detail);
}
