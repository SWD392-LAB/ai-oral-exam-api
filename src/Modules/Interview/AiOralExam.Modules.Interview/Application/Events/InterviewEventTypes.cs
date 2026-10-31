namespace AiOralExam.Modules.Interview.Application.Events;

internal static class InterviewEventTypes
{
    public const string AttemptStarted = "attempt.started";
    public const string AttemptResumed = "attempt.resumed";
    public const string TurnOpened = "turn.opened";
    public const string AnswerAccepted = "answer.accepted";
    public const string AnswerDuplicate = "answer.duplicate";
    public const string TurnDeadlineReached = "turn.deadline_reached";
    public const string TurnClosed = "turn.closed";
    public const string AttemptCompleted = "attempt.completed";
    public const string AttemptExpired = "attempt.expired";
    public const string ReviewFinalized = "review.finalized";
    public const string InterviewError = "InterviewError";
}
