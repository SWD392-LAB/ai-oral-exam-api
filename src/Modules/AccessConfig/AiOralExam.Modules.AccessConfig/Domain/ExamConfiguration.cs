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

/// <summary>Quan he N-N "teaches" giua COURSE va USER (Lecturer).</summary>
public class CourseLecturer
{
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public Guid LecturerId { get; set; }
    public User Lecturer { get; set; } = null!;
    public DateTime AssignedAt { get; set; }
}

/// <summary>ERD: EXAM SESSION (phien thi) - KHAC InterviewAttempt (luot thi cua tung sinh vien).</summary>
public class ExamSession
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public Guid CreatedBy { get; set; }
    public string Title { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    /// <summary>Giay.</summary>
    public int TimeLimitPerQuestion { get; set; }
    public int MaxFollowUps { get; set; } = 2;
    public ExamSessionStatus Status { get; set; } = ExamSessionStatus.Draft;
    public DateTime CreatedAt { get; set; }

    public ICollection<Question> Questions { get; set; } = new List<Question>();

    /// <summary>Da publish thi khoa cau hoi va rubric (quy tac nghiep vu #6).</summary>
    public bool IsLocked => Status != ExamSessionStatus.Draft;
}

/// <summary>ERD: QUESTION - cau hoi chinh do giang vien soan.</summary>
public class Question
{
    public Guid Id { get; set; }
    public Guid ExamSessionId { get; set; }
    public int OrderNo { get; set; }
    public string Content { get; set; } = null!;
    public Rubric Rubric { get; set; } = null!;
}

/// <summary>ERD: RUBRIC - dung 1 rubric cho moi cau hoi.</summary>
public class Rubric
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public string Criteria { get; set; } = null!;
    public decimal MaxScore { get; set; }
}
