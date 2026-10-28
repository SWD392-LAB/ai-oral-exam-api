using AiOralExam.Modules.Interview.Application.Events;
using AiOralExam.Modules.Interview.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AiOralExam.Modules.Interview.Infrastructure;

internal sealed class SignalRInterviewEventPublisher(
    IHubContext<InterviewUpdatesHub> hubContext,
    ILogger<SignalRInterviewEventPublisher> logger) : IInterviewEventPublisher
{
    public async Task PublishTranscriptUpdatedAsync(TranscriptUpdatedEvent transcriptEvent, CancellationToken ct = default)
    {
        try
        {
            await hubContext.Clients.Group(InterviewHubGroups.Attempt(transcriptEvent.AttemptId))
                .SendAsync("TranscriptUpdated", transcriptEvent, ct);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not publish transcript update for attempt {AttemptId}, turn {TurnId}.",
                transcriptEvent.AttemptId, transcriptEvent.TurnId);
        }
    }

    public async Task PublishAsync(InterviewAttemptEvent interviewEvent, CancellationToken ct = default)
    {
        try
        {
            var attemptGroup = hubContext.Clients.Group(InterviewHubGroups.Attempt(interviewEvent.AttemptId));
            if (interviewEvent.Type == "InterviewError")
            {
                await attemptGroup.SendAsync("InterviewError", interviewEvent, ct);
                return;
            }

            var clientEventName = interviewEvent.Type == "turn.opened" ? "QuestionReady" : "AttemptUpdated";
            await Task.WhenAll(
                attemptGroup.SendAsync("InterviewAttemptUpdated", interviewEvent, ct),
                attemptGroup.SendAsync(clientEventName, interviewEvent, ct),
                hubContext.Clients.Group(InterviewHubGroups.Session(interviewEvent.ExamSessionId))
                    .SendAsync("InterviewAttemptUpdated", interviewEvent, ct),
                hubContext.Clients.Group(InterviewHubGroups.Session(interviewEvent.ExamSessionId))
                    .SendAsync(clientEventName, interviewEvent, ct));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not publish interview event {EventId} for attempt {AttemptId}; clients can recover state through the GET endpoint.",
                interviewEvent.EventId, interviewEvent.AttemptId);
        }
    }
}
