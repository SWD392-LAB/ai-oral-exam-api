namespace AiOralExam.Modules.AccessConfig.Domain;

public class QuestionBankRevision
{
    public Guid Id { get; set; }
    public Guid QuestionBankItemId { get; set; }
    public QuestionBankItem Item { get; set; } = null!;
    public int RevisionNo { get; set; }
    public string Content { get; set; } = null!;
    public string? ProvenanceJson { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}
