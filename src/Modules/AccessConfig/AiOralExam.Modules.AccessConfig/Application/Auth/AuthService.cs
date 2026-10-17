using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.Auth;

/// <summary>Sign-in, passwords and password reset are handled by Keycloak; the API only reads the profile.</summary>
public interface IAuthService
{
    Task<UserProfileDto> GetProfileAsync(Guid userId, CancellationToken ct = default);
}

internal sealed class AuthService(AccessConfigDbContext db) : IAuthService
{
    public async Task<UserProfileDto> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new NotFoundException("user_not_found", "Account not found.");
        return new UserProfileDto(user.Id, user.FullName, user.Email, user.Role.Name, user.StudentCode, user.LecturerCode);
    }
}
