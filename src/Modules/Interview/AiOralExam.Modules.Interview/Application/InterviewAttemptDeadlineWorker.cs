using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiOralExam.Modules.Interview.Application;

internal sealed class InterviewAttemptDeadlineWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<InterviewAttemptDeadlineWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        RunPollingLoopAsync(ProcessIterationAsync, timeProvider, logger, PollInterval, stoppingToken);

    private async Task ProcessIterationAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var interview = scope.ServiceProvider.GetRequiredService<IInterviewService>();
        var expiredCount = await interview.ExpireOverdueAttemptsAsync(ct);
        if (expiredCount > 0)
            logger.LogInformation("Automatically processed {Count} expired interview turns.", expiredCount);
    }

    internal static async Task RunPollingLoopAsync(
        Func<CancellationToken, Task> processIteration,
        TimeProvider timeProvider,
        ILogger<InterviewAttemptDeadlineWorker> logger,
        TimeSpan pollInterval,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processIteration(stoppingToken);
            }
            catch (OperationCanceledException exception)
                when (stoppingToken.IsCancellationRequested && exception.CancellationToken == stoppingToken)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Failed to process expired interview turns. The next check will run after {PollInterval}.",
                    pollInterval);
            }

            await Task.Delay(pollInterval, timeProvider, stoppingToken);
        }
    }
}
