using System.ComponentModel.DataAnnotations;
using AiOralExam.Modules.AccessConfig.Domain;

namespace AiOralExam.Modules.AccessConfig.Application.Users.CreateUser;

public sealed record CreateUserRequest(
    [Required, StringLength(150)] string FullName,
    [EmailAddress, Required, StringLength(255)] string Email,
    [EnumDataType(typeof(UserRole))] UserRole Role,
    string? StudentCode,
    string? LecturerCode);
