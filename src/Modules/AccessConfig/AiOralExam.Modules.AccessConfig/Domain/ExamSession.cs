using AiOralExam.Modules.AccessConfig.Contracts;

namespace AiOralExam.Modules.AccessConfig.Domain;

/// <summary>ERD: EXAM SESSION - NOT the same as InterviewAttempt (one student's attempt).</summary>
public class ExamSession
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public RubricMode RubricMode { get; set; } = RubricMode.SharedVersion;
    public Guid? RubricVersionId { get; set; }
    public RubricVersion? RubricVersion { get; set; }
    public Guid CreatedBy { get; set; }
    public string Title { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    /// <summary>Seconds.</summary>
    public int TimeLimitPerAnswerSeconds { get; set; }
    public int MaxFollowUps { get; set; } = 2;
    public ExamSessionStatus Status { get; set; } = ExamSessionStatus.Draft;
    public DateTime CreatedAt { get; set; }

    public ICollection<Question> Questions { get; set; } = new List<Question>();

    /// <summary>Once published, questions and rubrics are locked (business rule #6).</summary>
    public bool IsLocked => Status != ExamSessionStatus.Draft;
}
