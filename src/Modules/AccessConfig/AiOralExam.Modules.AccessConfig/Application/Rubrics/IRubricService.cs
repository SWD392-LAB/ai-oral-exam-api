namespace AiOralExam.Modules.AccessConfig.Application.Rubrics;

public interface IRubricService
{
    Task<RubricDto> GetAsync(Guid sid, Guid qid, CancellationToken ct);
    Task<RubricDto> SaveAsync(Guid sid, Guid qid, SaveRubricRequest r, CancellationToken ct);
    Task DeleteAsync(Guid sid, Guid qid, CancellationToken ct);
}
