using System.Collections.Generic;
using System.Linq;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using ValidationException = AiOralExam.SharedKernel.Errors.ValidationException;

namespace AiOralExam.Modules.AccessConfig.Application.Users;

internal sealed record UserUpdateValues(
    string FullName,
    UserRole Role,
    string? StudentCode,
    string? LecturerCode,
    bool IsActive);

public sealed class UpdateUserService(AccessConfigDbContext db, ICurrentUser me)
{
    private const long AdministratorUpdateAdvisoryLock = 0x4149564553555345;

    internal static UserUpdateValues PrepareUpdate(User u, UpdateUserRequest r)
    {
        var currentRole = Enum.Parse<UserRole>(u.Role.Name);
        var role = r.Role ?? currentRole;
        var s = UserValidation.Clean(r.StudentCode);
        var l = UserValidation.Clean(r.LecturerCode);
        UserValidation.ValidateCodes(role, s, l);
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

    private static UserDto D(User u)
        => new(
        u.Id,
        u.FullName,
        u.Email,
        u.Username,
        u.Role.Name,
        u.StudentCode,
        u.LecturerCode,
        u.IsActive);

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
        var oldRoleName = u.Role.Name;
        var oldIsActive = u.IsActive;
        var oldFullName = u.FullName;
        var oldStudentCode = u.StudentCode;
        var oldLecturerCode = u.LecturerCode;
        ApplyUpdate(u, values, role);
        var roleChanged = oldRoleName != u.Role.Name;
        var deactivated = oldIsActive && !u.IsActive;
        var reactivated = !oldIsActive && u.IsActive;
        var profileChanged = oldFullName != u.FullName
            || (!roleChanged && (oldStudentCode != u.StudentCode || oldLecturerCode != u.LecturerCode));

        if (roleChanged)
            CourseService.AddAudit(db, me.Id, AuditActions.ChangeRole, AuditEntities.User, id,
                $"User {u.Username}: role {oldRoleName} -> {u.Role.Name}");
        if (deactivated)
            CourseService.AddAudit(db, me.Id, AuditActions.Deactivate, AuditEntities.User, id,
                $"User {u.Username}: deactivated");
        if (reactivated)
            CourseService.AddAudit(db, me.Id, AuditActions.Reactivate, AuditEntities.User, id,
                $"User {u.Username}: reactivated");
        if (profileChanged || (!roleChanged && !deactivated && !reactivated))
            CourseService.AddAudit(db, me.Id, AuditActions.Update, AuditEntities.User, id,
                $"User {u.Username} ({u.Role.Name})");
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return D(u);
    }
}
