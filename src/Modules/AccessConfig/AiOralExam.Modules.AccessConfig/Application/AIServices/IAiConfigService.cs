namespace AiOralExam.Modules.AccessConfig.Application.AIServices;

public interface IAiConfigService
{
    Task<IReadOnlyList<AiServiceConfigDto>> ListAsync(CancellationToken ct);
    Task<AiServiceConfigDto> GetAsync(Guid id, CancellationToken ct);
    Task<AiServiceConfigDto> SaveAsync(Guid? id, SaveAiServiceConfigRequest r, CancellationToken ct);
    Task<AiServiceConfigDto> SaveActiveAsync(SaveAiServiceConfigRequest r, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
