namespace AiOralExam.Modules.AccessConfig.Application.RubricTemplates;

public interface IRubricTemplateService
{
    Task<RubricTemplateDto> CreateAsync(CreateRubricTemplateRequest request, CancellationToken ct);
    Task<IReadOnlyList<RubricTemplateDto>> ListAsync(Guid? courseId, CancellationToken ct);
    Task<RubricTemplateDto> GetAsync(Guid id, CancellationToken ct);
    Task<RubricTemplateDto> UpdateAsync(Guid id, UpdateRubricTemplateRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
