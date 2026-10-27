namespace AiOralExam.Modules.AccessConfig.Application.RubricVersions;

public interface IRubricVersionService
{
    Task<RubricVersionDetailDto> CreateAsync(Guid templateId, CreateRubricVersionRequest request, CancellationToken ct);
    Task<RubricVersionDetailDto> ActivateAsync(Guid templateId, Guid versionId, CancellationToken ct);
    Task<IReadOnlyList<RubricVersionDto>> ListAsync(Guid templateId, CancellationToken ct);
    Task<RubricVersionDetailDto> GetAsync(Guid templateId, Guid versionId, CancellationToken ct);
}
