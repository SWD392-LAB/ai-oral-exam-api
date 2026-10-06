using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Contracts;

namespace AiOralExam.Modules.Reporting.Application;

/// <summary>Pure statistics calculations (no I/O) - easy to unit test.</summary>
internal static class ReportCalculator
{
    public const decimal GoodAnswerThreshold = 0.7m;

    public static decimal SuggestedTotal(AttemptResult a) =>
        a.Responses.Sum(r => r.SuggestedScore ?? 0);

    /// <summary>A final score exists only once the lecturer has Approved it.</summary>
    public static decimal? FinalScore(AttemptResult a) =>
        a.Review is { Status: ReviewStatus.Approved } r ? r.FinalScore : null;

    public static IReadOnlyList<ScoreBucketDto> Distribution(IEnumerable<decimal> scoresOnTen)
    {
        var buckets = new[] { "0-2", "2-4", "4-6", "6-8", "8-10" };
        var counts = new int[buckets.Length];
        foreach (var s in scoresOnTen)
        {
            var i = (int)Math.Floor(Math.Clamp(s, 0, 10) / 2);
            counts[Math.Min(i, buckets.Length - 1)]++;
        }
        return buckets.Select((b, i) => new ScoreBucketDto(b, counts[i])).ToList();
    }

    public static SessionStatisticsDto Statistics(ExamSessionInfo session, IReadOnlyList<AttemptResult> attempts)
    {
        var maxTotal = session.TotalMaxScore;
        var finals = attempts.Select(FinalScore).Where(s => s is not null).Select(s => s!.Value).ToList();

        var questions = session.Questions.Select(q =>
            {
                var answers = attempts
                    .SelectMany(a => a.Responses)
                    .Where(r => r.QuestionId == q.Id && r.SuggestedScore is not null)
                    .ToList();
                var avg = answers.Count == 0 ? 0 : answers.Average(r => r.SuggestedScore!.Value);
                var good = answers.Count == 0
                    ? 0
                    : (decimal)answers.Count(r => r.SuggestedScore!.Value >= q.MaxScore * GoodAnswerThreshold) / answers.Count;
                return new QuestionStatisticDto(
                    q.Id, q.OrderNo, q.Content, q.MaxScore, answers.Count,
                    Math.Round(avg, 2), Math.Round(good, 2),
                    answers.Count == 0 ? 0 : Math.Round(answers.Average(r => r.FollowUpCount), 2));
            })
            .OrderBy(x => x.AnswerCount == 0 ? decimal.MaxValue : x.AverageScore / x.MaxScore)
            .ToList();

        return new SessionStatisticsDto(
            session.Id,
            session.Title,
            attempts.Count,
            finals.Count,
            attempts.Count(a => a.Status == AttemptStatus.PendingReview),
            finals.Count == 0 ? null : Math.Round(finals.Average(), 2),
            maxTotal,
            Distribution(finals.Select(f => maxTotal == 0 ? 0 : f / maxTotal * 10)),
            questions);
    }
}
