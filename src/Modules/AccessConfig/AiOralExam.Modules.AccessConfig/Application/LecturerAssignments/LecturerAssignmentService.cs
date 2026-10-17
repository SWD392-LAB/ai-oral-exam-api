using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.LecturerAssignments;
public sealed record LecturerDto(
    Guid Id,
    string? FullName,
    string? Email,
    string? LecturerCode,
    DateTime AssignedAt);

public interface ILecturerAssignmentService
{
    Task<IReadOnlyList<LecturerDto>> ListAsync(Guid courseId, CancellationToken ct);
    Task ValidateLecturerAsync(Guid lecturerId, CancellationToken ct);
    Task<LecturerDto> AssignAsync(Guid courseId, Guid lecturerId, CancellationToken ct);
    Task RemoveAsync(Guid courseId, Guid lecturerId, CancellationToken ct);
}

internal sealed class LecturerAssignmentService(AccessConfigDbContext db, ICurrentUser me) : ILecturerAssignmentService
{
    async Task<User> LoadLecturerAsync(Guid lecturerId, CancellationToken ct)
    {
        var user = await db.Users.Include(x => x.Role)
            .FirstOrDefaultAsync(x => x.Id == lecturerId, ct) ??
            throw new NotFoundException("user_not_found", "Lecturer not found.");
        if (user.Role.Name != UserRole.Lecturer.ToString())
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lecturerId"] = ["User must have Lecturer role."]
            });
        if (!user.IsActive)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lecturerId"] = ["Lecturer must be active."]
            });
        return user;
    }

    public async Task ValidateLecturerAsync(Guid lecturerId, CancellationToken ct)
        => _ = await LoadLecturerAsync(lecturerId, ct);

    public async Task<IReadOnlyList<LecturerDto>> ListAsync(Guid courseId, CancellationToken ct)
        => await db.CourseLecturers.AsNoTracking()
        .Where(x => x.CourseId == courseId)
        .OrderBy(x => x.Lecturer.FullName)
        .Select(x => new LecturerDto(x.LecturerId, x.Lecturer.FullName, x.Lecturer.Email,
            x.Lecturer.LecturerCode, x.AssignedAt))
        .ToListAsync(ct);

    public async Task<LecturerDto> AssignAsync(Guid courseId, Guid lecturerId, CancellationToken ct)
    {
        var course = db.Courses.Local.FirstOrDefault(x => x.Id == courseId);
        if (course is null)
            course = await db.Courses.FirstOrDefaultAsync(x => x.Id == courseId, ct);
        if (course is null)
            throw new NotFoundException("course_not_found", "Course not found.");
        var u = await LoadLecturerAsync(lecturerId, ct);
        if (await db.CourseLecturers.AnyAsync(x => x.CourseId == courseId && x.LecturerId == lecturerId, ct))
            throw new ConflictException("lecturer_already_assigned", "Lecturer is already assigned.");
        var x = new CourseLecturer
        {
            CourseId = courseId,
            LecturerId = lecturerId,
            AssignedAt = DateTime.UtcNow
        };
        db.CourseLecturers.Add(x);
        CourseService.AddAudit(db, me.Id, AuditActions.AssignLecturer, AuditEntities.Course, courseId,
            $"Lecturer {u.Username} assigned to course {course.Code}");
        await db.SaveChangesAsync(ct);
        return new(u.Id, u.FullName, u.Email, u.LecturerCode, x.AssignedAt);
    }

    public async Task RemoveAsync(Guid courseId, Guid lecturerId, CancellationToken ct)
    {
        var x = await db.CourseLecturers.Include(x => x.Course).Include(x => x.Lecturer)
            .FirstOrDefaultAsync(x => x.CourseId == courseId && x.LecturerId == lecturerId, ct) ??
            throw new NotFoundException("assignment_not_found", "Lecturer assignment not found.");
        var description = $"Lecturer {x.Lecturer.Username} removed from course {x.Course.Code}";
        db.CourseLecturers.Remove(x);
        CourseService.AddAudit(db, me.Id, AuditActions.RemoveLecturer, AuditEntities.Course, courseId, description);
        await db.SaveChangesAsync(ct);
    }
}
