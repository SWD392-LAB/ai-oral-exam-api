namespace AiOralExam.Modules.AccessConfig.Application.Auth;

public sealed record UserProfileDto(
    Guid Id,
    string FullName,
    string Email,
    string Role,
    string? StudentCode,
    string? LecturerCode,
    string Username,
    bool IsActive);
