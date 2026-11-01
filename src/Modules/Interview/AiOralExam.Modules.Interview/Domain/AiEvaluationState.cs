namespace AiOralExam.Modules.Interview.Domain;

/// <summary>Durable state of the latest AI processing attempt for one response.</summary>
public enum AiEvaluationState
{
    NotStarted,
    Pending,
    Failed,
    Completed
}
