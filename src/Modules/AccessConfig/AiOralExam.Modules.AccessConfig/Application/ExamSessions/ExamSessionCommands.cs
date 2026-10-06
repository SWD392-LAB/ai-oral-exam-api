using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using ValidationException = AiOralExam.SharedKernel.Errors.ValidationException;


namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;
public sealed record CreateExamSessionRequest(
    Guid CourseId,
    [property: Required] string Title,
    DateTime StartTime,
    DateTime EndTime,
    [property: Range(1, 3600)] int TimeLimitPerQuestion,
    [property: Range(0, 20)] int MaxFollowUps = 2);

public sealed record UpdateExamSessionRequest(
    [property: Required] string Title,
    DateTime StartTime,
    DateTime EndTime,
    [property: Range(1, 3600)] int TimeLimitPerQuestion,
    [property: Range(0, 20)] int MaxFollowUps = 2);

public sealed record ExamSessionCommandDto(
    Guid Id,
    Guid CourseId,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    int TimeLimitPerQuestion,
    int MaxFollowUps,
    string Status,
    int QuestionCount,
    int ParticipantCount);

public interface IExamSessionCommands
{
    Task<ExamSessionCommandDto> CreateAsync(CreateExamSessionRequest r, CancellationToken ct);
    Task<ExamSessionCommandDto> UpdateAsync(Guid id, UpdateExamSessionRequest r, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<ExamSessionCommandDto> PublishAsync(Guid id, CancellationToken ct);
    Task<ExamSessionCommandDto> CloseAsync(Guid id, CancellationToken ct);
}

internal sealed class ExamSessionCommands(AccessConfigDbContext db, ICurrentUser me) : IExamSessionCommands
{
    async Task<ExamSession> Load(Guid id, CancellationToken ct)
    {
        var s = await db.ExamSessions
            .Include(x => x.Course).ThenInclude(x => x.Lecturers)
            .Include(x => x.Questions).ThenInclude(x => x.Rubric)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ??
            throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        if (!me.IsInRole(Roles.Administrator) && s.Course.Lecturers.All(x => x.LecturerId != me.Id))
            throw new ForbiddenException("You are not assigned to this course.");
        return s;
    }

    static DateTime Utc(DateTime x)
        => x.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(x, DateTimeKind.Utc) : x.ToUniversalTime();

    static ExamSessionCommandDto D(ExamSession s, int pc)
        => new(s.Id, s.CourseId, s.Title, s.StartTime, s.EndTime, s.TimeLimitPerQuestion, s.MaxFollowUps,
            s.Status.ToString(), s.Questions.Count, pc);

    public async Task<ExamSessionCommandDto> CreateAsync(CreateExamSessionRequest r, CancellationToken ct)
    {
        var c = await db.Courses.Include(x => x.Lecturers).FirstOrDefaultAsync(x => x.Id == r.CourseId, ct) ??
            throw new NotFoundException("course_not_found", "Course not found.");
        if (!me.IsInRole(Roles.Administrator) && c.Lecturers.All(x => x.LecturerId != me.Id))
            throw new ForbiddenException("You are not assigned to this course.");
        var start = Utc(r.StartTime); var end = Utc(r.EndTime); if (end <= start)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "endTime", ["EndTime must be after StartTime."]
                }
            });
        var s = new ExamSession
        {
            Id = Guid.NewGuid(),
            CourseId = c.Id,
            CreatedBy = me.Id,
            Title = r.Title.Trim(),
            StartTime = start,
            EndTime = end,
            TimeLimitPerQuestion = r.TimeLimitPerQuestion,
            MaxFollowUps = r.MaxFollowUps,
            Status = ExamSessionStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };
        db.ExamSessions.Add(s);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Create", "ExamSession", s.Id, ct);
        return D(s, 0);
    }
    public async Task<ExamSessionCommandDto> UpdateAsync(Guid id, UpdateExamSessionRequest r, CancellationToken ct)
    {
        var s = await Load(id, ct);
        if (s.Status != ExamSessionStatus.Draft)
            throw new ConflictException("exam_session_locked", "Only draft exam sessions can be edited.");
        var start = Utc(r.StartTime);
        var end = Utc(r.EndTime);
        if (end <= start)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "endTime", ["EndTime must be after StartTime."]
                }
            });
        s.Title = r.Title.Trim();
        s.StartTime = start; s.EndTime = end;
        s.TimeLimitPerQuestion = r.TimeLimitPerQuestion;
        s.MaxFollowUps = r.MaxFollowUps;
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Update", "ExamSession", id, ct);
        return D(s, await db.Participants.CountAsync(x => x.ExamSessionId == id, ct));
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var s = await Load(id, ct);
        if (s.Status != ExamSessionStatus.Draft)
            throw new ConflictException("exam_session_locked", "Only draft exam sessions can be deleted.");
        db.ExamSessions.Remove(s);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Delete", "ExamSession", id, ct);
    }

    public async Task<ExamSessionCommandDto> PublishAsync(Guid id, CancellationToken ct)
    {
        var s = await Load(id, ct);
        if (s.Status != ExamSessionStatus.Draft)
            throw new ConflictException("exam_session_not_draft", "Only draft exam sessions can be published.");
        if (s.Questions.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "questions", ["Add at least one question before publishing."]
                }
            });
        var missing = s.Questions.Where(x => x.Rubric is null)
            .Select(x => x.OrderNo)
            .OrderBy(x => x).ToList();
        if (missing.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "rubric", [$"Every question needs a rubric. Missing for question(s): " +
                    $"{string.Join(", ", missing)}."]
                }
            });
        var count = await db.Participants.CountAsync(x => x.ExamSessionId == id, ct);
        if (count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "participants", ["Add at least one student before publishing."]
                }
            });
        s.Status = ExamSessionStatus.Published;
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Publish", "ExamSession", id, ct);
        return D(s, count);
    }
    public async Task<ExamSessionCommandDto> CloseAsync(Guid id, CancellationToken ct)
    {
        var s = await Load(id, ct);
        if (s.Status != ExamSessionStatus.Published)
            throw new ConflictException("exam_session_not_published", "Only published exam sessions can be closed.");
        s.Status = ExamSessionStatus.Closed;
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Close", "ExamSession", id, ct);
        return D(s, await db.Participants.CountAsync(x => x.ExamSessionId == id, ct));
    }
}
