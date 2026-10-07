using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Auth;

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public sealed record UserProfileDto(
    Guid Id,
    string FullName,
    string Email,
    string Role,
    string? StudentCode,
    string? LecturerCode);

public sealed record LoginResponse(
    string AccessToken,
    DateTime ExpiresAt,
    UserProfileDto User);
