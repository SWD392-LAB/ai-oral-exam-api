using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.Modules.Interview.Domain;

/// <summary>
/// ERD: INTERVIEW ATTEMPT - luot thi cua MOT sinh vien (khac ExamSession).
/// Chuyen trang thai (BE-AI-01): InProgress -> PendingReview -> Finalized. Khong co chieu nguoc lai.
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
                $"Không thể chuyển lượt thi từ {Status} sang {next}.");
        Status = next;
    }

    /// <summary>Het cau hoi -> cho giang vien duyet.</summary>
    public void Complete(DateTime utcNow)
    {
        TransitionTo(AttemptStatus.PendingReview);
        CompletedAt = utcNow;
    }

    public void EnsureInProgress()
    {
        if (Status != AttemptStatus.InProgress)
            throw new ConflictException("attempt_not_in_progress", "Lượt thi đã kết thúc.");
    }

    /// <summary>Cau hoi dang hoi = response chua xong (moi luc chi co toi da 1).</summary>
    public QuestionResponse? CurrentResponse => Responses.FirstOrDefault(r => !r.IsFinished);
}
