using AiOralExam.Modules.AccessConfig.Contracts;

namespace AiOralExam.Modules.AccessConfig.Domain;

/// <summary>ERD: COURSE.</summary>
public class Course
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;

    public ICollection<CourseLecturer> Lecturers { get; set; } = new List<CourseLecturer>();
    public ICollection<ExamSession> ExamSessions { get; set; } = new List<ExamSession>();
}

/// <summary>N-N "teaches" relationship between COURSE and USER (Lecturer).</summary>
public class CourseLecturer
{
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public Guid LecturerId { get; set; }
    public User Lecturer { get; set; } = null!;
    public DateTime AssignedAt { get; set; }
}

/// <summary>ERD: EXAM SESSION - NOT the same as InterviewAttempt (one student's attempt).</summary>
public class ExamSession
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public Guid CreatedBy { get; set; }
    public string Title { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    /// <summary>Seconds.</summary>
    public int TimeLimitPerQuestion { get; set; }
    public int MaxFollowUps { get; set; } = 2;
    public ExamSessionStatus Status { get; set; } = ExamSessionStatus.Draft;
    public DateTime CreatedAt { get; set; }

    public ICollection<Question> Questions { get; set; } = new List<Question>();

    /// <summary>Once published, questions and rubrics are locked (business rule #6).</summary>
    public bool IsLocked => Status != ExamSessionStatus.Draft;
}

/// <summary>ERD: QUESTION - a main question written by the lecturer.</summary>
public class Question
{
    public Guid Id { get; set; }
    public Guid ExamSessionId { get; set; }
    public int OrderNo { get; set; }
    public string Content { get; set; } = null!;
    public Rubric Rubric { get; set; } = null!;
}

/// <summary>ERD: RUBRIC - exactly one rubric per question.</summary>
public class Rubric
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public string Criteria { get; set; } = null!;
    public decimal MaxScore { get; set; }
}

public enum ParticipantStatus
{
    Registered,
    InProgress,
    Completed
}

public class Participant
{
    public Guid Id { get; set; }
    public Guid ExamSessionId { get; set; }
    public ExamSession ExamSession { get; set; } = null!;
    public Guid StudentId { get; set; }
    public User Student { get; set; } = null!;
    public DateTime? JoinedAt { get; set; }
    public ParticipantStatus Status { get; set; } = ParticipantStatus.Registered;
}
