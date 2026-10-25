using AiOralExam.Modules.AccessConfig.Domain;

namespace AiOralExam.Modules.AccessConfig.Application.Users.UpdateUser;

internal sealed record UserUpdateValues(
    string FullName,
    UserRole Role,
    string? StudentCode,
    string? LecturerCode,
    bool IsActive);
