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

namespace AiOralExam.Modules.AccessConfig.Application.Questions;
public sealed record QuestionDto(Guid Id, int OrderNo, string Content, bool HasRubric);

public sealed record CreateQuestionRequest(
    [property: Range(1, int.MaxValue)] int OrderNo,
    [property: Required] string Content);

public sealed record UpdateQuestionRequest(
    [property: Range(1, int.MaxValue)] int OrderNo,
    [property: Required] string Content);

public interface IQuestionService
{
    Task<IReadOnlyList<QuestionDto>> ListAsync(Guid sessionId, CancellationToken ct);
    Task<QuestionDto> GetAsync(Guid sessionId, Guid id, CancellationToken ct);
    Task<QuestionDto> CreateAsync(Guid sessionId, CreateQuestionRequest r, CancellationToken ct);
    Task<QuestionDto> UpdateAsync(Guid sessionId, Guid id, UpdateQuestionRequest r, CancellationToken ct);
    Task DeleteAsync(Guid sessionId, Guid id, CancellationToken ct);
}

internal sealed class QuestionService(AccessConfigDbContext db, ICurrentUser me) : IQuestionService
{
    async Task<ExamSession> Editable(Guid id, CancellationToken ct)
    {
        var s = await db.ExamSessions.Include(x => x.Course).ThenInclude(x => x.Lecturers)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ??
            throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        if (s.Status != ExamSessionStatus.Draft)
            throw new ConflictException("exam_session_locked", "Only draft exam sessions can be edited.");
        if (!me.IsInRole(Roles.Administrator) && s.Course.Lecturers.All(x => x.LecturerId != me.Id))
            throw new ForbiddenException("You are not assigned to this course.");
        return s;
    }

    public async Task<IReadOnlyList<QuestionDto>> ListAsync(Guid sid, CancellationToken ct)
    {
        await Access(sid, ct);
        return await db.Questions.AsNoTracking().Where(x => x.ExamSessionId == sid)
            .OrderBy(x => x.OrderNo)
            .Select(x => new QuestionDto(x.Id, x.OrderNo, x.Content, x.Rubric != null))
            .ToListAsync(ct);
    }

    public async Task<QuestionDto> GetAsync(Guid sid, Guid id, CancellationToken ct)
    {
        await Access(sid, ct);
        return await db.Questions.AsNoTracking().Where(x => x.Id == id && x.ExamSessionId == sid)
            .Select(x => new QuestionDto(x.Id, x.OrderNo, x.Content, x.Rubric != null))
            .FirstOrDefaultAsync(ct) ??
            throw new NotFoundException("question_not_found", "Question not found.");
    }

    public async Task<QuestionDto> CreateAsync(Guid sid, CreateQuestionRequest r, CancellationToken ct)
    {
        await Editable(sid, ct);
        if (await db.Questions.AnyAsync(x => x.ExamSessionId == sid && x.OrderNo == r.OrderNo, ct))
            throw new ConflictException("question_order_exists", "Question order number already exists.");
        var q = new Question
        {
            Id = Guid.NewGuid(),
            ExamSessionId = sid,
            OrderNo = r.OrderNo,
            Content = r.Content.Trim()
        };
        db.Questions.Add(q);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Create", "Question", q.Id, ct);
        return new(q.Id, q.OrderNo, q.Content, false);
    }

    public async Task<QuestionDto> UpdateAsync(Guid sid, Guid id, UpdateQuestionRequest r, CancellationToken ct)
    {
        await Editable(sid, ct);
        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == id && x.ExamSessionId == sid, ct) ??
            throw new NotFoundException("question_not_found", "Question not found.");
        if (await db.Questions.AnyAsync(x => x.ExamSessionId == sid && x.OrderNo == r.OrderNo && x.Id != id, ct))
            throw new ConflictException("question_order_exists", "Question order number already exists.");
        q.OrderNo = r.OrderNo;
        q.Content = r.Content.Trim();
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Update", "Question", id, ct);
        return new(q.Id, q.OrderNo, q.Content, q.Rubric != null);
    }

    public async Task DeleteAsync(Guid sid, Guid id, CancellationToken ct)
    {
        await Editable(sid, ct);
        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == id && x.ExamSessionId == sid, ct) ??
            throw new NotFoundException("question_not_found", "Question not found.");
        db.Questions.Remove(q);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Delete", "Question", id, ct);
    }

    async Task Access(Guid sid, CancellationToken ct)
    {
        var s = await db.ExamSessions.AsNoTracking().Include(x => x.Course).ThenInclude(x => x.Lecturers)
            .FirstOrDefaultAsync(x => x.Id == sid, ct) ??
            throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        if (!me.IsInRole(Roles.Administrator) && s.Course.Lecturers.All(x => x.LecturerId != me.Id))
            throw new ForbiddenException("You are not assigned to this course.");
    }
}
