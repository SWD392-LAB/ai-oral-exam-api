using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ValidationException = AiOralExam.SharedKernel.Errors.ValidationException;

namespace AiOralExam.Modules.AccessConfig.Application.Users;
public sealed record UserDto(
    Guid Id,
    string FullName,
    string Email,
    string Role,
    string? StudentCode,
    string? LecturerCode,
    bool IsActive);

public sealed record CreateUserRequest(
    [Required] string FullName,
    [EmailAddress, Required] string Email,
    UserRole Role, string? StudentCode,
    string? LecturerCode, string? Password);

public sealed record UpdateUserRequest(
    [Required] string FullName,
    string? StudentCode,
    string? LecturerCode,
    bool IsActive);

public interface IUserService
{
    Task<IReadOnlyList<UserDto>> ListAsync(UserRole? role, bool? active, string? search, CancellationToken ct);
    Task<UserDto> GetAsync(Guid id, CancellationToken ct);
    Task<UserDto> CreateAsync(CreateUserRequest r, CancellationToken ct);
    Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest r, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
internal sealed class UserService(AccessConfigDbContext db, IPasswordHasher<User> hasher, ICurrentUser me) : IUserService
{
    static string? Clean(string? x)
        => string.IsNullOrWhiteSpace(x) ? null : x.Trim();

    static void ValidateCodes(UserRole role, string? s, string? l)
    {
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

    static UserDto D(User u)
        => new(
        u.Id,
        u.FullName,
        u.Email,
        u.RoleId.ToString(),
        u.StudentCode,
        u.LecturerCode,
        u.IsActive);

    public async Task<IReadOnlyList<UserDto>> ListAsync(UserRole? role, bool? active, string? search, CancellationToken ct)
    {
        var q = db.Users.AsNoTracking();
        if (role is not null)
            q = q.Where(x => x.RoleId == role);
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
        => D(await db.Users.FindAsync([id], ct) ??
            throw new NotFoundException("user_not_found", "User not found."));

    public async Task<UserDto> CreateAsync(CreateUserRequest r, CancellationToken ct)
    {
        var email = r.Email.Trim().ToLowerInvariant();
        var s = Clean(r.StudentCode);
        var l = Clean(r.LecturerCode);
        ValidateCodes(r.Role, s, l);
        if (await db.Users.AnyAsync(x => x.Email == email, ct))
            throw new ConflictException("email_exists", "Email is already in use.");
        if (s is not null && await db.Users.AnyAsync(x => x.StudentCode == s, ct))
            throw new ConflictException("student_code_exists", "Student code is already in use.");
        if (l is not null && await db.Users.AnyAsync(x => x.LecturerCode == l, ct))
            throw new ConflictException("lecturer_code_exists", "Lecturer code is already in use.");
        var u = new User
        {
            Id = Guid.NewGuid(),
            FullName = r.FullName.Trim(),
            Email = email,
            RoleId = r.Role,
            StudentCode = s,
            LecturerCode = l,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        if (!string.IsNullOrWhiteSpace(r.Password)) u.PasswordHash = hasher.HashPassword(u, r.Password);
        db.Users.Add(u);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Create", "User", u.Id, ct);
        return D(u);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest r, CancellationToken ct)
    {
        var u = await db.Users.FindAsync([id], ct) ??
            throw new NotFoundException("user_not_found", "User not found.");
        var s = Clean(r.StudentCode);
        var l = Clean(r.LecturerCode);
        ValidateCodes(u.RoleId, s, l);
        if (s is not null && await db.Users.AnyAsync(x => x.StudentCode == s && x.Id != id, ct))
            throw new ConflictException("student_code_exists", "Student code is already in use.");
        if (l is not null && await db.Users.AnyAsync(x => x.LecturerCode == l && x.Id != id, ct))
            throw new ConflictException("lecturer_code_exists", "Lecturer code is already in use.");
        u.FullName = r.FullName.Trim();
        u.StudentCode = s;
        u.LecturerCode = l;
        u.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Update", "User", id, ct);
        return D(u);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var u = await db.Users.FindAsync([id], ct) ??
            throw new NotFoundException("user_not_found", "User not found.");
        if (u.Id == me.Id)
            throw new ConflictException("cannot_delete_self", "You cannot delete your own account.");
        if (await db.CourseLecturers.AnyAsync(x => x.LecturerId == id, ct))
            throw new ConflictException("user_has_assignments", "Remove course assignments first.");
        db.Users.Remove(u);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Delete", "User", id, ct);
    }
}
