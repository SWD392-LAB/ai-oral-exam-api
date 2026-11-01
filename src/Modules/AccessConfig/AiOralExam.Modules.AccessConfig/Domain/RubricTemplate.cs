namespace AiOralExam.Modules.AccessConfig.Domain;

public class RubricTemplate
{
    public Guid Id { get; set; }
    public Guid? CourseId { get; set; }
    public Course? Course { get; set; }
    public string Name { get; set; } = null!;
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<RubricVersion> Versions { get; set; } = new List<RubricVersion>();
}
