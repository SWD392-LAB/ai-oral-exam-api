namespace AiOralExam.Modules.Interview.Domain;

public enum ParticipantStatus
{
    Registered,
    InProgress,
    Completed
}

/// <summary>ERD: PARTICIPANT - sinh vien duoc dang ky vao mot phien thi.</summary>
public class Participant
{
    public Guid Id { get; set; }
    public Guid ExamSessionId { get; set; }   // FK sang exam_sessions (module F7) - chi giu Id
    public Guid StudentId { get; set; }       // FK sang users (module F7) - chi giu Id
    public DateTime? JoinedAt { get; set; }
    public ParticipantStatus Status { get; set; } = ParticipantStatus.Registered;

    public ICollection<InterviewAttempt> Attempts { get; set; } = new List<InterviewAttempt>();
}
