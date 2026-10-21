namespace AiOralExam.Modules.AccessConfig.Domain;

/// <summary>ERD: AUDIT LOG.</summary>
public class AuditLog
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Action { get; set; } = null!;
    public string TargetEntity { get; set; } = null!;
    public Guid? TargetId { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}
