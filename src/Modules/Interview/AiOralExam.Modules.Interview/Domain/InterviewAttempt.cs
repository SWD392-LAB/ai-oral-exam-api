using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.Modules.Interview.Domain;

/// <summary>
/// ERD: INTERVIEW ATTEMPT - ONE student's attempt (not the same as ExamSession).
/// State transitions (BE-AI-01): InProgress -> PendingReview -> Finalized. Never backwards.
/// </summary>
public class InterviewAttempt
{
    public Guid Id { get; set; }
    public Guid ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public AttemptStatus Status { get; private set; } = AttemptStatus.InProgress;

    public ICollection<QuestionResponse> Responses { get; set; } = new List<QuestionResponse>();
    public ScoreReview? Review { get; set; }

    public static bool CanTransition(AttemptStatus from, AttemptStatus to) => (from, to) switch
    {
        (AttemptStatus.InProgress, AttemptStatus.PendingReview) => true,
        (AttemptStatus.PendingReview, AttemptStatus.Finalized) => true,
        _ => false
    };

    public void TransitionTo(AttemptStatus next)
    {
        if (!CanTransition(Status, next))
            throw new ConflictException("invalid_attempt_transition",
                $"Cannot move the attempt from {Status} to {next}.");
        Status = next;
    }

    /// <summary>No questions left -> waits for lecturer review.</summary>
    public void Complete(DateTime utcNow)
    {
        TransitionTo(AttemptStatus.PendingReview);
        CompletedAt = utcNow;
    }

    public void EnsureInProgress()
    {
        if (Status != AttemptStatus.InProgress)
            throw new ConflictException("attempt_not_in_progress", "This attempt has already ended.");
    }

    /// <summary>The question being asked = the unfinished response (at most one at any time).</summary>
    public QuestionResponse? CurrentResponse => Responses.FirstOrDefault(r => !r.IsFinished);
}
