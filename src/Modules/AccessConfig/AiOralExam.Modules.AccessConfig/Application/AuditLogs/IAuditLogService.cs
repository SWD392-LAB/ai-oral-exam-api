namespace AiOralExam.Modules.AccessConfig.Application.AuditLogs;

public interface IAuditLogService
{
    Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid? userId, string? action, string? targetEntity, DateTime? from, DateTime? to, int take, CancellationToken ct);
}
