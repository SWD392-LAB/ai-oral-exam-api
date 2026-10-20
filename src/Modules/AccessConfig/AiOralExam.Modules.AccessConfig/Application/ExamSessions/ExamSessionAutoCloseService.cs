using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

internal sealed class ExamSessionAutoCloseService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ExamSessionAutoCloseService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            int closedCount;

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
                closedCount = await FindExpiredPublishedSessions(db, now)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(session => session.Status, ExamSessionStatus.Closed),
                        stoppingToken);
            }

            if (closedCount > 0)
                logger.LogInformation("Automatically closed {Count} expired exam sessions.", closedCount);

            await Task.Delay(PollInterval, timeProvider, stoppingToken);
        }
    }

    internal static IQueryable<ExamSession> FindExpiredPublishedSessions(
        AccessConfigDbContext db,
        DateTime utcNow) =>
        db.ExamSessions.Where(session =>
            session.Status == ExamSessionStatus.Published && session.EndTime <= utcNow);
}
