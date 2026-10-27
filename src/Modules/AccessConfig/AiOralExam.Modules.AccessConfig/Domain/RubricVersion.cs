namespace AiOralExam.Modules.AccessConfig.Domain;

public class RubricVersion
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public RubricTemplate Template { get; set; } = null!;
    public Guid CourseId { get; set; }
    public int VersionNo { get; set; }
    public decimal MaxScore { get; set; }
    public bool IsSealed { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<RubricCriterion> Criteria { get; set; } = new List<RubricCriterion>();
}
