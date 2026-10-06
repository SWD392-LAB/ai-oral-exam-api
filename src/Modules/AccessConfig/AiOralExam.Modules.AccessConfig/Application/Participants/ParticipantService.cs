using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using ValidationException = AiOralExam.SharedKernel.Errors.ValidationException;

namespace AiOralExam.Modules.AccessConfig.Application.Participants;
public sealed record ParticipantDto(
    Guid Id,
    Guid StudentId,
    string StudentCode,
    string StudentName,
    string Email,
    string Status,
    DateTime? JoinedAt);

public sealed record ImportStudentsRequest(
    [property: Required] IReadOnlyList<string> StudentCodes);

public sealed record ImportStudentsResult(
    int Imported,
    int Skipped,
    IReadOnlyList<string> SkippedStudentCodes,
    IReadOnlyList<ParticipantDto> Participants);

public interface IParticipantService
{
    Task<IReadOnlyList<ParticipantDto>> ListAsync(Guid sessionId, CancellationToken ct);
    Task<ImportStudentsResult> ImportAsync(Guid sessionId, ImportStudentsRequest r, CancellationToken ct);
    Task RemoveAsync(Guid sessionId, Guid participantId, CancellationToken ct);
}

public sealed class ParticipantService(AccessConfigDbContext db, ICurrentUser me) : IParticipantService
{
    async Task<ExamSession> Editable(Guid sid, CancellationToken ct)
    {
        var s = await db.ExamSessions.Include(x => x.Course)
            .ThenInclude(x => x.Lecturers)
            .FirstOrDefaultAsync(x => x.Id == sid, ct) ??
            throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        if (s.Status != ExamSessionStatus.Draft)
            throw new ConflictException("exam_session_locked", "Participants can only be changed while the exam is Draft.");
        if (!me.IsInRole(Roles.Administrator) && s.Course.Lecturers.All(x => x.LecturerId != me.Id))
            throw new ForbiddenException("You are not assigned to this course.");
        return s;
    }

    public async Task<IReadOnlyList<ParticipantDto>> ListAsync(Guid sid, CancellationToken ct)
    {
        var s = await db.ExamSessions.AsNoTracking()
            .Include(x => x.Course)
            .ThenInclude(x => x.Lecturers)
            .FirstOrDefaultAsync(x => x.Id == sid, ct) ??
            throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        if (!me.IsInRole(Roles.Administrator) && s.Course.Lecturers.All(x => x.LecturerId != me.Id))
            throw new ForbiddenException("You are not assigned to this course.");
        return await db.Participants.AsNoTracking()
            .Where(x => x.ExamSessionId == sid)
            .OrderBy(x => x.Student.FullName)
            .Select(x => new ParticipantDto(x.Id, x.StudentId, x.Student.StudentCode!, x.Student.FullName,
                                            x.Student.Email, x.Status.ToString(), x.JoinedAt)).ToListAsync(ct);
    }

    public async Task<ImportStudentsResult> ImportAsync(Guid sid, ImportStudentsRequest r, CancellationToken ct)
    {
        await Editable(sid, ct);
        var codes = r.StudentCodes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant()).Distinct().ToList();
        if (codes.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "studentCodes", ["Provide at least one student code."]
                }
            });
        var students = await db.Users.Where(x => x.RoleId == UserRole.Student && x.StudentCode != null &&
                                            codes.Contains(x.StudentCode)).ToListAsync(ct);
        var found = students.Select(x => x.StudentCode!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = codes.Where(x => !found.Contains(x)).ToList();
        var existing = (await db.Participants.Where(x => x.ExamSessionId == sid)
            .Select(x => x.StudentId).ToListAsync(ct)).ToHashSet();
        var add = students.Where(x => !existing.Contains(x.Id))
            .Select(x => new Participant
            {
                Id = Guid.NewGuid(),
                ExamSessionId = sid,
                StudentId = x.Id,
                Status = ParticipantStatus.Registered
            }).ToList();
        db.Participants.AddRange(add);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "ImportStudents", "ExamSession", sid, ct);
        return new(add.Count, skipped.Count, skipped, await ListAsync(sid, ct));
    }

    public async Task RemoveAsync(Guid sid, Guid pid, CancellationToken ct)
    {
        await Editable(sid, ct);
        var p = await db.Participants.FirstOrDefaultAsync(x => x.Id == pid && x.ExamSessionId == sid, ct) ??
            throw new NotFoundException("participant_not_found", "Participant not found.");
        db.Participants.Remove(p);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "RemoveParticipant", "ExamSession", sid, ct);
    }
}
