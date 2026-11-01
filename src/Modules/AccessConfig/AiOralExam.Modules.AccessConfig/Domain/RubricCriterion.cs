namespace AiOralExam.Modules.AccessConfig.Domain;

using AiOralExam.Modules.AccessConfig.Domain;

/// <summary>ERD: RUBRIC CRITERION - one scoring item of a rubric.</summary>
public class RubricCriterion
{
    public Guid Id { get; set; }
    public Guid? RubricId { get; set; }
    public Rubric? Rubric { get; set; }
    public Guid? RubricVersionId { get; set; }
    public RubricVersion? RubricVersion { get; set; }
    public int OrderNo { get; set; }
    public string Description { get; set; } = null!;
    public decimal MaxPoints { get; set; }
}
