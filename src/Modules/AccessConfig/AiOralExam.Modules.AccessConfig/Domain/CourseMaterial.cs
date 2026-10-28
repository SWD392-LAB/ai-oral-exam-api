namespace AiOralExam.Modules.AccessConfig.Domain;

public class CourseMaterial
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = null!;
    public string StorageKey { get; set; } = null!;
    public CourseMaterialStatus Status { get; set; } = CourseMaterialStatus.Pending;
    public string? IngestError { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public ICollection<MaterialChunk> Chunks { get; set; } = new List<MaterialChunk>();
}
