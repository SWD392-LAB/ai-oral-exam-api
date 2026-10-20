using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.UnitTests;

public class ExamSessionAutoCloseTests
{
    [Fact]
    public async Task Finds_published_sessions_at_or_past_end_time_only()
    {
        await using var db = new AccessConfigDbContext(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var expired = Session(ExamSessionStatus.Published, now.AddTicks(-1));
        var endsNow = Session(ExamSessionStatus.Published, now);
        var notExpired = Session(ExamSessionStatus.Published, now.AddTicks(1));
        var draft = Session(ExamSessionStatus.Draft, now.AddTicks(-1));
        var alreadyClosed = Session(ExamSessionStatus.Closed, now.AddTicks(-1));
        db.ExamSessions.AddRange(expired, endsNow, notExpired, draft, alreadyClosed);
        await db.SaveChangesAsync();

        var selected = await ExamSessionAutoCloseService.FindExpiredPublishedSessions(db, now)
            .Select(session => session.Id)
            .ToListAsync();

        Assert.Equal(
            new[] { expired.Id, endsNow.Id }.OrderBy(id => id),
            selected.OrderBy(id => id));
    }

    [Fact]
    public void Session_is_not_open_at_or_after_end_time()
    {
        var endTime = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var session = new ExamSessionInfo(
            Guid.NewGuid(), Guid.NewGuid(), "SWD392", "Software Development", "Session",
            endTime.AddHours(-1), endTime, 120, 2, ExamSessionStatus.Published, []);

        Assert.True(session.IsOpenAt(endTime.AddTicks(-1)));
        Assert.False(session.IsOpenAt(endTime));
        Assert.False(session.IsOpenAt(endTime.AddTicks(1)));
    }

    private static ExamSession Session(ExamSessionStatus status, DateTime endTime) => new()
    {
        Id = Guid.NewGuid(),
        CourseId = Guid.NewGuid(),
        CreatedBy = Guid.NewGuid(),
        Title = "Session",
        StartTime = endTime.AddHours(-1),
        EndTime = endTime,
        TimeLimitPerAnswerSeconds = 120,
        Status = status
    };
}
