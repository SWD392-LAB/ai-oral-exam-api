using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Auth;

public sealed record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password);

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
