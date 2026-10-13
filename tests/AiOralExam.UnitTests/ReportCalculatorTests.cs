using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Reporting.Application;

namespace AiOralExam.UnitTests;

public class ReportCalculatorTests
{
    [Fact]
    public void Distribution_puts_scores_in_two_point_buckets()
    {
        var buckets = ReportCalculator.Distribution([0m, 1.9m, 2m, 7.5m, 10m, 9.99m]);

        Assert.Equal(new[] { "0-2", "2-4", "4-6", "6-8", "8-10" }, buckets.Select(b => b.Range));
        Assert.Equal(new[] { 2, 1, 0, 1, 2 }, buckets.Select(b => b.Count));
    }

    [Fact]
    public void Statistics_exclude_pending_scores_and_count_unanswered_questions_as_zero()
    {
        var question = new QuestionInfo(Guid.NewGuid(), 1, "Answered question", [], 10);
        var unanswered = new QuestionInfo(Guid.NewGuid(), 2, "Unanswered question", [], 10);
        var ungraded = new QuestionInfo(Guid.NewGuid(), 3, "Draft question without rubric", [], 0);
        var session = new ExamSessionInfo(Guid.NewGuid(), Guid.NewGuid(), "SWD392", "Design", "Viva",
            DateTime.UtcNow, DateTime.UtcNow.AddHours(1), 120, 2, ExamSessionStatus.Closed,
            [question, unanswered, ungraded]);
        var first = Attempt(AttemptStatus.Finalized, 8,
            [new(Guid.NewGuid(), question.Id, 2, true, 10, "Good", [])]);
        var second = Attempt(AttemptStatus.Finalized, 0, []);
        var pending = Attempt(AttemptStatus.PendingReview, null,
            [new(Guid.NewGuid(), question.Id, 8, true, 9, "Not confirmed", [])]);

        var stats = ReportCalculator.Statistics(session, [first, second, pending]);

        Assert.Equal(3, stats.CompletedAttempts);
        Assert.Equal(2, stats.FinalizedAttempts);
        Assert.Equal(1, stats.PendingReviewAttempts);
        Assert.Equal(4m, stats.AverageFinalScore);
        Assert.Equal(2, stats.ScoreDistribution.Sum(b => b.Count));
        var graded = Assert.Single(stats.Questions, q => q.QuestionId == question.Id);
        Assert.Equal(2, graded.AnswerCount);
        Assert.Equal(5m, graded.AverageScore);
        Assert.Equal(0.5m, graded.GoodAnswerRate);
        Assert.Equal(1d, graded.AverageFollowUps);
        Assert.Equal(unanswered.Id, stats.Questions[0].QuestionId);
        Assert.Equal(0m, stats.Questions[0].AverageScore);
        Assert.Equal(ungraded.Id, stats.Questions[^1].QuestionId);
        Assert.Equal(0m, stats.Questions[^1].GoodAnswerRate);

        var waiting = ReportCalculator.Statistics(session, [pending]);
        Assert.Null(waiting.AverageFinalScore);
        Assert.All(waiting.Questions, q => Assert.Equal(0, q.AnswerCount));
        Assert.All(waiting.ScoreDistribution, b => Assert.Equal(0, b.Count));
    }

    private static AttemptResult Attempt(AttemptStatus status, decimal? finalScore,
        IReadOnlyList<QuestionResponseResult> responses) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), status, DateTime.UtcNow, DateTime.UtcNow,
            finalScore is null ? null : new(Guid.NewGuid(), finalScore.Value, null, ReviewStatus.Approved, DateTime.UtcNow),
            responses);
}
