using AiOralExam.Modules.Interview.Contracts;

namespace AiOralExam.Modules.Interview.Domain;

/// <summary>ERD: PARTICIPANT - a student registered for an exam session.</summary>
public class Participant
{
    public Guid Id { get; set; }
    public Guid ExamSessionId { get; set; }   // FK to exam_sessions (module F7) - id only
    public Guid StudentId { get; set; }       // FK to users (module F7) - id only
    /// <summary>When the student was added to the session's list.</summary>
    public DateTime JoinedAt { get; set; }
    public ParticipantStatus Status { get; set; } = ParticipantStatus.Registered;

    public ICollection<InterviewAttempt> Attempts { get; set; } = new List<InterviewAttempt>();

    public void ReconcileStatus()
    {
        Status = Attempts.Any(attempt => attempt.Status == AttemptStatus.InProgress)
            ? ParticipantStatus.InProgress
            : Attempts.Any(attempt => attempt.Status is AttemptStatus.PendingReview or AttemptStatus.Finalized)
                ? ParticipantStatus.Completed
                : ParticipantStatus.Registered;
    }
}
