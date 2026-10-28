using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Materials;

public sealed record CourseMaterialDto(
    Guid Id,
    Guid CourseId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string Status,
    string? IngestError,
    DateTime CreatedAt);

public sealed record MaterialChunkSource(Guid MaterialId, int ChunkNo, string Citation, string Text);

public interface IMaterialService
{
    Task<CourseMaterialDto> UploadAsync(Guid courseId, string fileName, string contentType, Stream content, long? declaredLength, CancellationToken ct);
    Task<IReadOnlyList<CourseMaterialDto>> ListAsync(Guid courseId, CancellationToken ct);
    Task<CourseMaterialDto> IngestAsync(Guid courseId, Guid materialId, CancellationToken ct);
    Task<IReadOnlyList<MaterialChunkSource>> RetrieveAsync(Guid courseId, IReadOnlyCollection<Guid> materialIds, string topic, CancellationToken ct);
}

public sealed class MaterialOptions
{
    public const string SectionName = "QuestionBank";
    public string StorageRoot { get; set; } = "material-storage";
    public long MaxBytes { get; set; } = 25 * 1024 * 1024;
    public int ChunkSize { get; set; } = 1200;
}
