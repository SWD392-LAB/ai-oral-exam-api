namespace AiOralExam.Modules.Interview.Application.Ai.Mock;

/// <summary>
/// Mock answer analysis: deterministic, the same input always gives the same result.
/// An answer shorter than MinWords words (or empty) => insufficient, ask a follow-up if any are left.
/// Toggle with Interview:Mock:EnableFollowUps (off by default in M1).
/// </summary>
internal sealed class MockAnswerAnalyzer(bool enableFollowUps) : IAnswerAnalyzer
{
    private const int MinWords = 15;

    public Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default)
    {
        var lastAnswer = request.Dialogue.LastOrDefault()?.Answer;
        var words = MockText.CountWords(lastAnswer);
        var sufficient = words >= MinWords;

        var needsFollowUp = enableFollowUps && !sufficient && request.FollowUpsUsed < request.MaxFollowUps;
        var followUp = needsFollowUp
            ? (string.IsNullOrWhiteSpace(lastAnswer)
                ? "You have not answered the question yet. Could you briefly explain your main idea?"
                : "Could you explain that in more detail and give a concrete example?")
            : null;

        IReadOnlyList<string> missing = sufficient ? [] : ["The answer is short and lacks an explanation or an example."];
        return Task.FromResult(new AnswerAnalysis(sufficient, missing, needsFollowUp, followUp));
    }
}
