namespace AiOralExam.Modules.AccessConfig.Application.Materials;

public interface IMaterialStorage
{
    Task<string> PutAsync(Guid courseId, Guid materialId, string fileName, Stream content, CancellationToken ct);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}
