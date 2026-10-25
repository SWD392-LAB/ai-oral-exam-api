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
