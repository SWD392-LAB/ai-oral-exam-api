namespace AiOralExam.Modules.AccessConfig.Application.Users;

public sealed record CreatedUserDto(
    Guid Id,
    string? FullName,
    string? Email,
    string Username,
    string Role,
    string? StudentCode,
    string? LecturerCode,
    bool IsActive,
    bool SetupEmailSent);
