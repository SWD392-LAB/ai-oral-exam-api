namespace AiOralExam.Modules.AccessConfig.Application.Auth;

/// <summary>Sign-in, passwords and password reset are handled by Keycloak; the API only reads the profile.</summary>
public interface IAuthService
{
    Task<UserProfileDto> GetProfileAsync(Guid userId, CancellationToken ct = default);
}
