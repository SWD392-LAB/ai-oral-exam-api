using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Application.Keycloak;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ValidationException = AiOralExam.SharedKernel.Errors.ValidationException;

namespace AiOralExam.Modules.AccessConfig.Application.Users;

public sealed class CreateUserService(
    AccessConfigDbContext db,
    ICurrentUser me,
    TimeProvider timeProvider,
    IKeycloakAdminClient keycloak,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<CreateUserService>();

    private static string UsernameFromEmail(string email)
        => email[..email.IndexOf('@')].ToLowerInvariant();

    public async Task<CreatedUserDto> CreateAsync(CreateUserRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.FullName))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["fullName"] = ["A full name is required."]
            });

        var fullName = r.FullName.Trim();
        var email = r.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["email"] = ["A valid email address is required."]
            });

        var username = UsernameFromEmail(email);
        if (username.Length > 50)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["email"] = ["The email local part must not exceed 50 characters."]
            });

        var s = UserValidation.Clean(r.StudentCode);
        var l = UserValidation.Clean(r.LecturerCode);
        UserValidation.ValidateCodes(r.Role, s, l);
        if (await db.Users.AnyAsync(x => x.Email.ToLower() == email, ct))
            throw new ConflictException("email_exists", "Email is already in use.");
        if (await db.Users.AnyAsync(x => x.Username == username, ct))
            throw new ConflictException("username_exists", "Username is already in use.");
        var role = await db.Roles.FirstOrDefaultAsync(x => x.Name == r.Role.ToString(), ct) ??
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["role"] = ["The requested role is not configured."]
            });
        if (s is not null && await db.Users.AnyAsync(x => x.StudentCode == s, ct))
            throw new ConflictException("student_code_exists", "Student code is already in use.");
        if (l is not null && await db.Users.AnyAsync(x => x.LecturerCode == l, ct))
            throw new ConflictException("lecturer_code_exists", "Lecturer code is already in use.");

        var nameParts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var firstName = nameParts.Length == 1 ? nameParts[0] : string.Join(' ', nameParts[..^1]);
        var lastName = nameParts.Length == 1 ? string.Empty : nameParts[^1];
        var keycloakId = await keycloak.CreateUserAsync(username, email, firstName, lastName, ct);
        var u = new User
        {
            Id = keycloakId,
            Username = username,
            FullName = fullName,
            Email = email,
            RoleId = role.Id,
            Role = role,
            StudentCode = s,
            LecturerCode = l,
            IsActive = true,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime
        };
        try
        {
            await keycloak.AssignRealmRoleAsync(keycloakId, r.Role.ToString(), ct);
            db.Users.Add(u);
            CourseService.AddAudit(db, me.Id, AuditActions.Create, AuditEntities.User, u.Id,
                $"User {u.Username} ({u.Role.Name})");
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            try
            {
                await keycloak.DeleteUserAsync(keycloakId, CancellationToken.None);
            }
            catch (Exception deleteException)
            {
                logger.LogWarning(deleteException, "Could not remove Keycloak user {UserId} after user creation failed.", keycloakId);
            }

            if (ex is DbUpdateException
                {
                    InnerException: PostgresException
                    {
                        SqlState: PostgresErrorCodes.UniqueViolation,
                        ConstraintName: "ux_users_email"
                    }
                })
                throw new ConflictException("email_exists", "Email is already in use.");
            throw;
        }

        var setupEmailSent = false;
        try
        {
            setupEmailSent = await keycloak.SendSetPasswordEmailAsync(keycloakId, ct);
        }
        catch (AppException ex)
        {
            logger.LogWarning("Could not send a password setup email for user {UserId}; Keycloak error {ErrorCode}.",
                keycloakId, ex.Code);
        }
        return new CreatedUserDto(u.Id, u.FullName, u.Email, u.Username, u.Role.Name,
            u.StudentCode, u.LecturerCode, u.IsActive, setupEmailSent);
    }
}
