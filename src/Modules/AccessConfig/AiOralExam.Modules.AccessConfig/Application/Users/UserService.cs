using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
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
public sealed record UserDto(
    Guid Id,
    string? FullName,
    string? Email,
    string Username,
    string Role,
    string? StudentCode,
    string? LecturerCode,
    bool IsActive);

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

public sealed record CreateUserRequest(
    [Required, StringLength(150)] string FullName,
    [EmailAddress, Required, StringLength(255)] string Email,
    [EnumDataType(typeof(UserRole))] UserRole Role,
    string? StudentCode,
    string? LecturerCode);

public sealed record UpdateUserRequest(
    [Required] string FullName,
    string? StudentCode,
    string? LecturerCode,
    bool IsActive,
    UserRole? Role = null);

public sealed record UpdateOwnProfileRequest(
    [Required, StringLength(150)] string FullName);

internal sealed record UserUpdateValues(
    string FullName,
    UserRole Role,
    string? StudentCode,
    string? LecturerCode,
    bool IsActive);

public interface IUserService
{
    Task<IReadOnlyList<UserDto>> ListAsync(UserRole? role, bool? active, string? search, CancellationToken ct);
    Task<UserDto> GetAsync(Guid id, CancellationToken ct);
    Task<CreatedUserDto> CreateAsync(CreateUserRequest r, CancellationToken ct);
    Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest r, CancellationToken ct);
    Task ResendSetupEmailAsync(Guid id, Guid actorId, CancellationToken ct);
    Task UpdateOwnProfileAsync(Guid currentUserId, UpdateOwnProfileRequest request, CancellationToken ct);
}
internal sealed class UserService(
    AccessConfigDbContext db,
    ICurrentUser me,
    TimeProvider timeProvider,
    IKeycloakAdminClient keycloak,
    ILogger<UserService> logger) : IUserService
{
    private const long AdministratorUpdateAdvisoryLock = 0x4149564553555345;

    static string UsernameFromEmail(string email)
        => email[..email.IndexOf('@')].ToLowerInvariant();

    static string? Clean(string? x)
        => string.IsNullOrWhiteSpace(x) ? null : x.Trim();

    static void ValidateCodes(UserRole role, string? s, string? l)
    {
        if (!Enum.IsDefined(role))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["role"] = ["A valid user role is required."]
            });
        if (role == UserRole.Student && string.IsNullOrWhiteSpace(s))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "studentCode", ["Student requires a student code."]
                }
            });
        if (role == UserRole.Lecturer && string.IsNullOrWhiteSpace(l))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "lecturerCode", ["Lecturer requires a lecturer code."]
                }
            });
        if (role != UserRole.Student && s is not null)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "studentCode", ["Only students can have a student code."]
                }
            });
        if (role != UserRole.Lecturer && l is not null)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "lecturerCode", ["Only lecturers can have a lecturer code."]
                }
            });
    }

    internal static UserUpdateValues PrepareUpdate(User u, UpdateUserRequest r)
    {
        var currentRole = Enum.Parse<UserRole>(u.Role.Name);
        var role = r.Role ?? currentRole;
        var s = Clean(r.StudentCode);
        var l = Clean(r.LecturerCode);
        ValidateCodes(role, s, l);
        return new UserUpdateValues(r.FullName.Trim(), role, s, l, r.IsActive);
    }

    internal static void ApplyUpdate(User u, UserUpdateValues values, Role role)
    {
        u.FullName = values.FullName;
        u.Role = role;
        u.RoleId = role.Id;
        u.StudentCode = values.StudentCode;
        u.LecturerCode = values.LecturerCode;
        u.IsActive = values.IsActive;
    }

    internal static bool IsSelfModification(
        Guid targetId, Guid currentUserId, UserRole currentRole, bool isActive, UpdateUserRequest request)
        => targetId == currentUserId
           && ((request.Role is UserRole requestedRole && requestedRole != currentRole)
               || (isActive && !request.IsActive));

    internal static bool RequiresAdministratorUpdateLock(
        UserRole currentRole, bool isActive, UpdateUserRequest request)
    {
        var requestedRole = request.Role ?? currentRole;
        return currentRole == UserRole.Administrator && isActive
            && (requestedRole != UserRole.Administrator || !request.IsActive);
    }

    internal static bool HasOtherActiveAdministrator(Guid targetId, IEnumerable<User> administrators)
        => administrators.Any(x => x.Id != targetId && x.IsActive
            && x.Role.Name == UserRole.Administrator.ToString());

    internal static string? GetUpdateConflictCode(
        Guid targetId,
        Guid currentUserId,
        UserRole currentRole,
        bool isActive,
        UpdateUserRequest request,
        IEnumerable<User> administrators)
    {
        if (IsSelfModification(targetId, currentUserId, currentRole, isActive, request))
            return "cannot_modify_self";
        if (RequiresAdministratorUpdateLock(currentRole, isActive, request)
            && !HasOtherActiveAdministrator(targetId, administrators))
            return "last_administrator";
        return null;
    }

    static UserDto D(User u)
        => new(
        u.Id,
        u.FullName,
        u.Email,
        u.Username,
        u.Role.Name,
        u.StudentCode,
        u.LecturerCode,
        u.IsActive);

    public async Task<IReadOnlyList<UserDto>> ListAsync(UserRole? role, bool? active, string? search, CancellationToken ct)
    {
        IQueryable<User> q = db.Users.AsNoTracking().Include(x => x.Role);
        if (role is not null)
            q = q.Where(x => x.Role.Name == role.ToString());
        if (active is not null)
            q = q.Where(x => x.IsActive == active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var p = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.FullName, p) ||
            EF.Functions.ILike(x.Email, p) ||
            EF.Functions.ILike(x.StudentCode ?? "", p) ||
            EF.Functions.ILike(x.LecturerCode ?? "", p));
        }
        var rows = await q.OrderBy(x => x.FullName).ToListAsync(ct);
        return rows.Select(D).ToList();
    }

    public async Task<UserDto> GetAsync(Guid id, CancellationToken ct)
        => D(await db.Users.Include(x => x.Role)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ??
            throw new NotFoundException("user_not_found", "User not found."));

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

        var s = Clean(r.StudentCode);
        var l = Clean(r.LecturerCode);
        ValidateCodes(r.Role, s, l);
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

        var setupEmailSent = await keycloak.SendSetPasswordEmailAsync(keycloakId, ct);
        return new CreatedUserDto(u.Id, u.FullName, u.Email, u.Username, u.Role.Name,
            u.StudentCode, u.LecturerCode, u.IsActive, setupEmailSent);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest r, CancellationToken ct)
    {
        var u = await db.Users.Include(x => x.Role)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ??
            throw new NotFoundException("user_not_found", "User not found.");
        var currentRole = Enum.Parse<UserRole>(u.Role.Name);
        if (IsSelfModification(id, me.Id, currentRole, u.IsActive, r))
            throw new ConflictException("cannot_modify_self", "You cannot change your own role or deactivate your account.");

        var values = PrepareUpdate(u, r);
        var requiresAdministratorLock = RequiresAdministratorUpdateLock(currentRole, u.IsActive, r);
        await using var transaction = requiresAdministratorLock
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        if (transaction is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({AdministratorUpdateAdvisoryLock})", ct);

            await db.Entry(u).ReloadAsync(ct);
            if (db.Entry(u).State == EntityState.Detached)
                throw new NotFoundException("user_not_found", "User not found.");
            db.Entry(u).Reference(x => x.Role).IsLoaded = false;
            await db.Entry(u).Reference(x => x.Role).LoadAsync(ct);

            currentRole = Enum.Parse<UserRole>(u.Role.Name);
            if (IsSelfModification(id, me.Id, currentRole, u.IsActive, r))
                throw new ConflictException("cannot_modify_self", "You cannot change your own role or deactivate your account.");
            values = PrepareUpdate(u, r);

            if (RequiresAdministratorUpdateLock(currentRole, u.IsActive, r))
            {
                var administrators = await db.Users.Include(x => x.Role)
                    .Where(x => x.Role.Name == UserRole.Administrator.ToString())
                    .ToListAsync(ct);
                var conflictCode = GetUpdateConflictCode(
                    id, me.Id, currentRole, u.IsActive, r, administrators);
                if (conflictCode == "last_administrator")
                    throw new ConflictException("last_administrator", "At least one other active administrator must remain.");
            }
        }

        if (values.StudentCode is not null && await db.Users.AnyAsync(x => x.StudentCode == values.StudentCode && x.Id != id, ct))
            throw new ConflictException("student_code_exists", "Student code is already in use.");
        if (values.LecturerCode is not null && await db.Users.AnyAsync(x => x.LecturerCode == values.LecturerCode && x.Id != id, ct))
            throw new ConflictException("lecturer_code_exists", "Lecturer code is already in use.");
        var role = values.Role == Enum.Parse<UserRole>(u.Role.Name)
            ? u.Role
            : await db.Roles.FirstOrDefaultAsync(x => x.Name == values.Role.ToString(), ct)
                ?? throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["role"] = ["The requested role is not configured."]
                });
        ApplyUpdate(u, values, role);
        CourseService.AddAudit(db, me.Id, AuditActions.Update, AuditEntities.User, id,
            $"User {u.Username} ({u.Role.Name})");
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return D(u);
    }

    public async Task ResendSetupEmailAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ??
            throw new NotFoundException("user_not_found", "User not found.");
        if (!user.IsActive)
            throw new ConflictException("user_inactive", "Cannot send a setup email to an inactive user.");
        if (!await keycloak.SendSetPasswordEmailAsync(user.Id, ct))
            throw new AppException(502, "setup_email_failed", "Keycloak could not send the password setup email.");

        CourseService.AddAudit(db, actorId, AuditActions.ResendSetupEmail, AuditEntities.User, id,
            $"User {user.Username}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Updates the display name in the application database only; the Keycloak profile is not synchronized.</summary>
    public async Task UpdateOwnProfileAsync(
        Guid currentUserId, UpdateOwnProfileRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == currentUserId, ct) ??
            throw new NotFoundException("user_not_found", "Account not found.");
        if (string.IsNullOrWhiteSpace(request.FullName))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["fullName"] = ["A full name is required."]
            });

        var fullName = request.FullName.Trim();
        if (fullName.Length > 150)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["fullName"] = ["The full name must not exceed 150 characters."]
            });

        user.FullName = fullName;
        CourseService.AddAudit(db, currentUserId, AuditActions.UpdateOwnProfile, AuditEntities.User, user.Id,
            $"User {user.Username}");
        await db.SaveChangesAsync(ct);
    }

}
