using System.ComponentModel.DataAnnotations;
using AiOralExam.Modules.AccessConfig.Domain;

namespace AiOralExam.Modules.AccessConfig.Application.Users.UpdateUser;

public sealed record UpdateUserRequest(
    [Required] string FullName,
    string? StudentCode,
    string? LecturerCode,
    bool IsActive,
    UserRole? Role = null);
