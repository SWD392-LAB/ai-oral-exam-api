namespace AiOralExam.Modules.AccessConfig.Domain;

public class Participant
{
    public Guid Id { get; set; }
    public Guid ExamSessionId { get; set; }
    public ExamSession ExamSession { get; set; } = null!;
    public Guid StudentId { get; set; }
    public User Student { get; set; } = null!;
    public DateTime JoinedAt { get; set; }
    public ParticipantStatus Status { get; set; } = ParticipantStatus.Registered;
}
