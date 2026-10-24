namespace AiOralExam.Modules.Interview.Domain;

/// <summary>ERD: CRITERION SCORE - the points the AI gives one rubric criterion for one question response.</summary>
public class CriterionScore
{
    public Guid Id { get; set; }
    public Guid QuestionResponseId { get; set; }
    public Guid RubricCriterionId { get; set; }   // FK to rubric_criteria (module F7) - id only
    public decimal Points { get; set; }
    public string? Comment { get; set; }
}
