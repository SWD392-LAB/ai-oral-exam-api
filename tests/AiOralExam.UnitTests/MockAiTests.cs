using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Application.Ai.Mock;

namespace AiOralExam.UnitTests;

public class MockAiTests
{
    private static RubricScoringRequest Request(string? answer) =>
        new("Q", "criteria", 10, [new DialogueTurn("Q", answer, false)], "Vietnamese");

    [Fact]
    public async Task Scorer_is_deterministic()
    {
        var scorer = new MockRubricScorer();
        var answer = string.Join(' ', Enumerable.Repeat("word", 30));

        var a = await scorer.ScoreAsync(Request(answer));
        var b = await scorer.ScoreAsync(Request(answer));

        Assert.Equal(a, b);
        Assert.Equal(5m, a.SuggestedScore);
    }

    [Fact]
    public async Task Scorer_gives_zero_for_no_answer()
    {
        var result = await new MockRubricScorer().ScoreAsync(Request(null));
        Assert.Equal(0m, result.SuggestedScore);
    }

    [Fact]
    public async Task Analyzer_asks_follow_up_only_when_enabled_and_quota_left()
    {
        var req = new AnswerAnalysisRequest("Q", "c", [new DialogueTurn("Q", "too short", false)], 0, 2, "Vietnamese");

        Assert.True((await new MockAnswerAnalyzer(true).AnalyzeAsync(req)).NeedsFollowUp);
        Assert.False((await new MockAnswerAnalyzer(false).AnalyzeAsync(req)).NeedsFollowUp);
        Assert.False((await new MockAnswerAnalyzer(true).AnalyzeAsync(req with { FollowUpsUsed = 2 })).NeedsFollowUp);
    }
}
