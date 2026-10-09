using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.LecturerAssignments;
public sealed record LecturerDto(
    Guid Id,
    string FullName,
    string Email,
    string? LecturerCode,
    DateTime AssignedAt);

public interface ILecturerAssignmentService
{
    Task<IReadOnlyList<LecturerDto>> ListAsync(Guid courseId, CancellationToken ct);
    Task<LecturerDto> AssignAsync(Guid courseId, Guid lecturerId, CancellationToken ct);
    Task RemoveAsync(Guid courseId, Guid lecturerId, CancellationToken ct);
}

internal sealed class LecturerAssignmentService(AccessConfigDbContext db, ICurrentUser me) : ILecturerAssignmentService
{
    public async Task<IReadOnlyList<LecturerDto>> ListAsync(Guid courseId, CancellationToken ct)
        => await db.CourseLecturers.AsNoTracking()
        .Where(x => x.CourseId == courseId)
        .OrderBy(x => x.Lecturer.FullName)
        .Select(x => new LecturerDto(x.LecturerId, x.Lecturer.FullName, x.Lecturer.Email,
            x.Lecturer.LecturerCode, x.AssignedAt))
        .ToListAsync(ct);

    public async Task<LecturerDto> AssignAsync(Guid courseId, Guid lecturerId, CancellationToken ct)
    {
        if (!await db.Courses.AnyAsync(x => x.Id == courseId, ct))
            throw new NotFoundException("course_not_found", "Course not found.");
        var u = await db.Users.FindAsync([lecturerId], ct) ??
            throw new NotFoundException("user_not_found", "Lecturer not found.");
        if (u.RoleId != UserRole.Lecturer)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "lecturerId", ["User must have Lecturer role."]
                }
            });
        if (await db.CourseLecturers.AnyAsync(x => x.CourseId == courseId && x.LecturerId == lecturerId, ct))
            throw new ConflictException("lecturer_already_assigned", "Lecturer is already assigned.");
        var x = new CourseLecturer
        {
            CourseId = courseId,
            LecturerId = lecturerId,
            AssignedAt = DateTime.UtcNow
        };
        db.CourseLecturers.Add(x);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "AssignLecturer", "Course", courseId, ct);
        return new(u.Id, u.FullName, u.Email, u.LecturerCode, x.AssignedAt);
    }

    public async Task RemoveAsync(Guid courseId, Guid lecturerId, CancellationToken ct)
    {
        var x = await db.CourseLecturers.FirstOrDefaultAsync(x => x.CourseId == courseId && x.LecturerId == lecturerId, ct) ??
            throw new NotFoundException("assignment_not_found", "Lecturer assignment not found.");
        db.CourseLecturers.Remove(x);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "RemoveLecturer", "Course", courseId, ct);
    }
}
