namespace AiOralExam.Modules.AccessConfig.Contracts;

public sealed record AuditLogEntry(
    Guid UserId,
    string Action,
    string TargetEntity,
    Guid? TargetId,
    string? Description);

public interface IAuditLogWriter
{
    Task AppendAsync(AuditLogEntry entry, CancellationToken ct = default);
}

public static class AuditLogActions
{
    public const string ManualRecoverySucceeded = "ManualRecoverySucceeded";
    public const string ManualRecoveryFailed = "ManualRecoveryFailed";
}

public static class AuditLogEntities
{
    public const string InterviewAttempt = "InterviewAttempt";
}
