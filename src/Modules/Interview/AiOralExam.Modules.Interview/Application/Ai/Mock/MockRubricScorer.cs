namespace AiOralExam.Modules.Interview.Application.Ai.Mock;

/// <summary>
/// Mock scoring (BE-AI-04): every criterion gets the same share of its max points, based on the
/// word count of all answers, rounded to 0.5. Deterministic: same input => same scores + feedback.
/// </summary>
internal sealed class MockRubricScorer : IRubricScorer
{
    private const int WordsForFullScore = 60;

    public Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default)
    {
        var words = request.Dialogue.Sum(t => MockText.CountWords(t.Answer));
        var ratio = Math.Min(1m, (decimal)words / WordsForFullScore);

        var feedback = ratio switch
        {
            0m => "[Mock AI] The student did not answer the question.",
            < 0.4m => "[Mock AI] The answer is too short and does not meet the rubric criteria.",
            < 0.8m => "[Mock AI] Covers some of the main points; needs a deeper explanation and an example.",
            _ => "[Mock AI] Complete answer with an explanation and an example that fit the rubric."
        };
        var comment = ratio == 1m ? "Met." : ratio == 0m ? "Not met." : "Partially met.";

        var scores = request.Criteria
            .Select(c => new CriterionPoints(
                c.Id, Math.Round(c.MaxPoints * ratio * 2, MidpointRounding.AwayFromZero) / 2, comment))
            .ToList();

        return Task.FromResult(new RubricScore(scores, feedback));
    }
}
