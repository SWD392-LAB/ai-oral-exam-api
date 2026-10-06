using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiOralExam.Modules.AccessConfig.Application.Auth;

public interface IAuthService
{
    Task<LoginResponse> LoginWithPasswordAsync(LoginRequest request, CancellationToken ct = default);
    Task<UserProfileDto> GetProfileAsync(Guid userId, CancellationToken ct = default);

    // TODO (M4): LoginWithGoogleAsync(idToken), RequestPasswordResetAsync(email), SetPasswordAsync(token, password)
}

internal sealed class AuthService(
    AccessConfigDbContext db,
    IPasswordHasher<User> passwordHasher,
    IJwtTokenIssuer tokenIssuer,
    ILogger<AuthService> logger) : IAuthService
{
    // Cung 1 thong bao cho moi truong hop sai -> khong lo email nao co tai khoan (R-08)
    private static UnauthorizedException InvalidCredentials() =>
        new("invalid_credentials", "Email hoặc mật khẩu không đúng.");

    public async Task<LoginResponse> LoginWithPasswordAsync(LoginRequest request, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null || !user.IsActive || user.PasswordHash is null)
        {
            logger.LogInformation("Login failed for {Email}", email);
            throw InvalidCredentials();
        }

        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            logger.LogInformation("Login failed for user {UserId}", user.Id);
            throw InvalidCredentials();
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
            await db.SaveChangesAsync(ct);
        }

        var (token, expiresAt) = tokenIssuer.Issue(user);
        logger.LogInformation("User {UserId} logged in as {Role}", user.Id, user.RoleId);
        return new LoginResponse(token, expiresAt, ToDto(user));
    }

    public async Task<UserProfileDto> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new NotFoundException("user_not_found", "Không tìm thấy tài khoản.");
        return ToDto(user);
    }

    private static UserProfileDto ToDto(User u) =>
        new(u.Id, u.FullName, u.Email, u.RoleId.ToString(), u.StudentCode, u.LecturerCode);
}
