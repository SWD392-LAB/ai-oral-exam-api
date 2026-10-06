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
}
