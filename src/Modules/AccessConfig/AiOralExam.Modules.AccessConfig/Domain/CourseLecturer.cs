namespace AiOralExam.Modules.AccessConfig.Domain;

/// <summary>N-N "teaches" relationship between COURSE and USER (Lecturer).</summary>
public class CourseLecturer
{
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public Guid LecturerId { get; set; }
    public User Lecturer { get; set; } = null!;
    public DateTime AssignedAt { get; set; }
}
