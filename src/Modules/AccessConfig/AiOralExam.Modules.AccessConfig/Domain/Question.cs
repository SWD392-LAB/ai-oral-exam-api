namespace AiOralExam.Modules.AccessConfig.Domain;

/// <summary>ERD: QUESTION - a main question written by the lecturer.</summary>
public class Question
{
    public Guid Id { get; set; }
    public Guid ExamSessionId { get; set; }
    public int OrderNo { get; set; }
    public string Content { get; set; } = null!;

    /// <summary>Null while the session is a draft and the lecturer has not defined the rubric yet.</summary>
    public Rubric? Rubric { get; set; }
}
