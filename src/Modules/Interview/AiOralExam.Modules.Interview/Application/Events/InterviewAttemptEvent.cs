namespace AiOralExam.Modules.Interview.Application.Events;

using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;

public sealed record InterviewAttemptEvent(
    Guid EventId,
    Guid AttemptId,
    Guid ExamSessionId,
    long Sequence,
    string Type,
    DateTime OccurredAt);

public interface IInterviewEventPublisher
{
    Task PublishAsync(InterviewAttemptEvent interviewEvent, CancellationToken ct = default);
}

internal static class InterviewEventSequence
{
    public static long AttemptStarted => 1;

    public static long TurnOpened(int turnCount) => turnCount * 2L;

    public static long TurnAnswered(int turnCount) => turnCount * 2L + 1;

    public static long AttemptCompleted(int turnCount) => turnCount * 2L + 2;

    public static long Current(InterviewAttempt attempt)
    {
        var turnCount = attempt.Responses.Sum(response => response.Turns.Count);
        return attempt.Status switch
        {
            AttemptStatus.InProgress => TurnOpened(turnCount),
            AttemptStatus.PendingReview => AttemptCompleted(turnCount),
            AttemptStatus.Finalized => AttemptCompleted(turnCount) + 1,
            _ => AttemptStarted
        };
    }
}
