namespace AiOralExam.Modules.AccessConfig.Application.AuditLogs;

public sealed record AuditLogDto(
    Guid Id,
    Guid UserId,
    string Action,
    string TargetEntity,
    Guid? TargetId,
    DateTime CreatedAt,
    string? Description,
    string? UserName,
    string? Email);
