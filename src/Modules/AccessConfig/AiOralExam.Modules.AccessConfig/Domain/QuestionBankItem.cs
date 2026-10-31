namespace AiOralExam.Modules.AccessConfig.Domain;

public class QuestionBankItem
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public string Topic { get; set; } = null!;
    public string Bloom { get; set; } = null!;
    public string Content { get; set; } = null!;
    public string ContentHash { get; set; } = null!;
    public QuestionBankOrigin Origin { get; set; }
    public QuestionBankStatus Status { get; set; } = QuestionBankStatus.Draft;
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<QuestionBankRevision> Revisions { get; set; } = new List<QuestionBankRevision>();
}
