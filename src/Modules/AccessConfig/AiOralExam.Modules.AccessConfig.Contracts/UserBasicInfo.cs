namespace AiOralExam.Modules.AccessConfig.Contracts;

public sealed record UserBasicInfo(
    Guid Id,
    string FullName,
    string Email,
    string? StudentCode);
