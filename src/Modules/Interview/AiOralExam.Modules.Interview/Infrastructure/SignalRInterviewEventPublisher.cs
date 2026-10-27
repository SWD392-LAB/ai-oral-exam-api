using AiOralExam.Modules.Interview.Application.Events;
using AiOralExam.Modules.Interview.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AiOralExam.Modules.Interview.Infrastructure;

internal sealed class SignalRInterviewEventPublisher(
    IHubContext<InterviewUpdatesHub> hubContext,
    ILogger<SignalRInterviewEventPublisher> logger) : IInterviewEventPublisher
{
    public async Task PublishAsync(InterviewAttemptEvent interviewEvent, CancellationToken ct = default)
    {
        try
        {
            await Task.WhenAll(
                hubContext.Clients.Group(InterviewHubGroups.Attempt(interviewEvent.AttemptId))
                    .SendAsync("InterviewAttemptUpdated", interviewEvent, ct),
                hubContext.Clients.Group(InterviewHubGroups.Session(interviewEvent.ExamSessionId))
                    .SendAsync("InterviewAttemptUpdated", interviewEvent, ct));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not publish interview event {EventId} for attempt {AttemptId}; clients can recover state through the GET endpoint.",
                interviewEvent.EventId, interviewEvent.AttemptId);
        }
    }
}
