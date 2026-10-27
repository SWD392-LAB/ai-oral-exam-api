using AiOralExam.Modules.Interview.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AiOralExam.Modules.Interview.Hubs;

[Authorize]
public sealed class InterviewUpdatesHub(
    IInterviewService interview) : Hub
{
    public async Task SubscribeAttempt(Guid attemptId)
    {
        await interview.AuthorizeAttemptSubscriptionAsync(attemptId, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, InterviewHubGroups.Attempt(attemptId), Context.ConnectionAborted);
    }

    public async Task SubscribeSession(Guid examSessionId)
    {
        await interview.AuthorizeSessionSubscriptionAsync(examSessionId, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, InterviewHubGroups.Session(examSessionId), Context.ConnectionAborted);
    }

    public async Task UnsubscribeAttempt(Guid attemptId)
    {
        await interview.AuthorizeAttemptSubscriptionAsync(attemptId, Context.ConnectionAborted);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, InterviewHubGroups.Attempt(attemptId), Context.ConnectionAborted);
    }

    public async Task UnsubscribeSession(Guid examSessionId)
    {
        await interview.AuthorizeSessionSubscriptionAsync(examSessionId, Context.ConnectionAborted);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, InterviewHubGroups.Session(examSessionId), Context.ConnectionAborted);
    }
}

internal static class InterviewHubGroups
{
    public static string Attempt(Guid attemptId) => $"interview-attempt:{attemptId:N}";

    public static string Session(Guid sessionId) => $"interview-session:{sessionId:N}";
}
