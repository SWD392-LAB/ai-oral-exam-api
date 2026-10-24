namespace AiOralExam.Modules.AccessConfig.Domain;

/// <summary>ERD: RUBRIC - at most one rubric per question, made of criteria.</summary>
public class Rubric
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }

    /// <summary>Sum of Criteria.MaxPoints, kept in sync by a trigger in 01_schema.sql.</summary>
    public decimal MaxScore { get; set; }

    public ICollection<RubricCriterion> Criteria { get; set; } = new List<RubricCriterion>();
}
