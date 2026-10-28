using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Application.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiOralExam.UnitTests;

public sealed class AiOutputValidationTests
{
    private static readonly RubricCriterionInfo[] Criteria =
    [
        new(Guid.NewGuid(), 1, "Explain the idea", 6),
        new(Guid.NewGuid(), 2, "Give an example", 4),
    ];

    private static RubricScoringRequest ScoreRequest() =>
        new("Question", Criteria, [new DialogueTurn("Question", "answer", false)], "Vietnamese");

    private static AnswerAnalysisRequest AnalysisRequest() =>
        new("Question", Criteria, [new DialogueTurn("Question", "answer", false)], 0, 2, "Vietnamese");

    [Fact]
    public async Task Scorer_rejects_unknown_criterion_instead_of_allowing_it_to_become_zero()
    {
        var output = new RubricScore(
        [
            new CriterionPoints(Criteria[0].Id, 2, "ok"),
            new CriterionPoints(Guid.NewGuid(), 2, "unknown"),
        ], "feedback");

        var error = await Assert.ThrowsAsync<AiEvaluationException>(() =>
            new ValidatedRubricScorer(new StubScorer(output), NullLogger<ValidatedRubricScorer>.Instance)
                .ScoreAsync(ScoreRequest()));

        Assert.Equal("ai_evaluation_failed", error.Code);
    }

    [Fact]
    public async Task Scorer_rejects_scores_outside_criterion_range()
    {
        var output = new RubricScore(
        [
            new CriterionPoints(Criteria[0].Id, 7, "too high"),
            new CriterionPoints(Criteria[1].Id, 2, "ok"),
        ], "feedback");

        await Assert.ThrowsAsync<AiEvaluationException>(() =>
            new ValidatedRubricScorer(new StubScorer(output), NullLogger<ValidatedRubricScorer>.Instance)
                .ScoreAsync(ScoreRequest()));
    }

    [Fact]
    public async Task Analyzer_rejects_follow_up_after_quota()
    {
        var output = new AnswerAnalysis(false, ["missing"], true, "Follow up?");
        var request = AnalysisRequest() with { FollowUpsUsed = 2 };

        await Assert.ThrowsAsync<AiEvaluationException>(() =>
            new ValidatedAnswerAnalyzer(new StubAnalyzer(output), NullLogger<ValidatedAnswerAnalyzer>.Instance)
                .AnalyzeAsync(request));
    }

    [Fact]
    public async Task Provider_exception_is_sanitized_for_retry()
    {
        var error = await Assert.ThrowsAsync<AiEvaluationException>(() =>
            new ValidatedRubricScorer(new ThrowingScorer(), NullLogger<ValidatedRubricScorer>.Instance)
                .ScoreAsync(ScoreRequest()));

        Assert.Equal("ai_evaluation_failed", error.Code);
        Assert.DoesNotContain("api-key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubScorer(RubricScore result) : IRubricScorer
    {
        public Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingScorer : IRubricScorer
    {
        public Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default) =>
            Task.FromException<RubricScore>(new InvalidOperationException("api-key leaked"));
    }

    private sealed class StubAnalyzer(AnswerAnalysis result) : IAnswerAnalyzer
    {
        public Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default) =>
            Task.FromResult(result);
    }
}
