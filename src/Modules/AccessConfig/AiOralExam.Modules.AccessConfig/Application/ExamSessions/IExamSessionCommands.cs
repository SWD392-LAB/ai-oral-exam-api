namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

public interface IExamSessionCommands
{
    Task<ExamSessionCommandDto> CreateAsync(CreateExamSessionRequest r, CancellationToken ct);
    Task<ExamSessionCommandDto> UpdateAsync(Guid id, UpdateExamSessionRequest r, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<ExamSessionCommandDto> PublishAsync(Guid id, CancellationToken ct);
    Task<ExamSessionCommandDto> CloseAsync(Guid id, CancellationToken ct);
}
