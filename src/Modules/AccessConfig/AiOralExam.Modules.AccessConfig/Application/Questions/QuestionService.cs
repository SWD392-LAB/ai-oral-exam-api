using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using ValidationException = AiOralExam.SharedKernel.Errors.ValidationException;

namespace AiOralExam.Modules.AccessConfig.Application.Questions;
public sealed record QuestionDto(Guid Id, int OrderNo, string Content, bool HasRubric);

public sealed record CreateQuestionRequest(
    [Range(1, int.MaxValue)] int OrderNo,
    [Required] string Content);

public sealed record ImportQuestionsRequest([Required] IReadOnlyList<CreateQuestionRequest> Questions);

public sealed record UpdateQuestionRequest(
    [Range(1, int.MaxValue)] int OrderNo,
    [Required] string Content);

public interface IQuestionService
{
    Task<IReadOnlyList<QuestionDto>> ListAsync(Guid sessionId, CancellationToken ct);
    Task<QuestionDto> GetAsync(Guid sessionId, Guid id, CancellationToken ct);
    Task<QuestionDto> CreateAsync(Guid sessionId, CreateQuestionRequest r, CancellationToken ct);
    Task<IReadOnlyList<QuestionDto>> ImportAsync(Guid sessionId, ImportQuestionsRequest r, CancellationToken ct);
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
        var session = await Editable(sid, ct);
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
        CourseService.AddAudit(db, me.Id, AuditActions.Create, AuditEntities.Question, q.Id,
            $"Question in {session.Title}");
        await db.SaveChangesAsync(ct);
        return new(q.Id, q.OrderNo, q.Content, false);
    }

    public async Task<IReadOnlyList<QuestionDto>> ImportAsync(Guid sid, ImportQuestionsRequest r, CancellationToken ct)
    {
        var session = await Editable(sid, ct);
        var errors = new Dictionary<string, string[]>();
        if (r.Questions is null || r.Questions.Count == 0)
            errors["questions"] = ["At least one question is required."];
        else
        {
            for (var i = 0; i < r.Questions.Count; i++)
            {
                var question = r.Questions[i];
                if (question is null)
                {
                    errors[$"questions[{i}]"] = ["Question cannot be null."];
                    continue;
                }
                if (question.OrderNo <= 0)
                    errors[$"questions[{i}].orderNo"] = ["OrderNo must be greater than 0."];
                if (string.IsNullOrWhiteSpace(question.Content))
                    errors[$"questions[{i}].content"] = ["Content is required."];
            }

            foreach (var group in r.Questions
                .Select((question, index) => (question, index))
                .Where(x => x.question is not null && x.question.OrderNo > 0)
                .GroupBy(x => x.question.OrderNo)
                .Where(x => x.Count() > 1))
            {
                foreach (var item in group)
                    errors[$"questions[{item.index}].orderNo"] = ["OrderNo must be unique within the import."];
            }
        }

        if (errors.Count > 0)
            throw new ValidationException(errors);

        var questions = r.Questions!;
        var orderNumbers = questions.Select(x => x.OrderNo).ToArray();
        var existingOrderNumbers = await db.Questions
            .Where(x => x.ExamSessionId == sid && orderNumbers.Contains(x.OrderNo))
            .Select(x => x.OrderNo)
            .ToListAsync(ct);
        foreach (var item in questions.Select((question, index) => (question, index)))
        {
            if (existingOrderNumbers.Contains(item.question.OrderNo))
                errors[$"questions[{item.index}].orderNo"] = ["OrderNo already exists in this exam session."];
        }
        if (errors.Count > 0)
            throw new ValidationException(errors);

        var imported = questions.Select(item => new Question
        {
            Id = Guid.NewGuid(),
            ExamSessionId = sid,
            OrderNo = item.OrderNo,
            Content = item.Content.Trim()
        }).ToList();
        db.Questions.AddRange(imported);
        foreach (var question in imported)
            CourseService.AddAudit(db, me.Id, AuditActions.Create, AuditEntities.Question, question.Id,
                $"Question in {session.Title}");
        await db.SaveChangesAsync(ct);

        return imported.Select(x => new QuestionDto(x.Id, x.OrderNo, x.Content, false)).ToList();
    }

    public async Task<QuestionDto> UpdateAsync(Guid sid, Guid id, UpdateQuestionRequest r, CancellationToken ct)
    {
        var session = await Editable(sid, ct);
        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == id && x.ExamSessionId == sid, ct) ??
            throw new NotFoundException("question_not_found", "Question not found.");
        if (await db.Questions.AnyAsync(x => x.ExamSessionId == sid && x.OrderNo == r.OrderNo && x.Id != id, ct))
            throw new ConflictException("question_order_exists", "Question order number already exists.");
        q.OrderNo = r.OrderNo;
        q.Content = r.Content.Trim();
        CourseService.AddAudit(db, me.Id, AuditActions.Update, AuditEntities.Question, id,
            $"Question in {session.Title}");
        await db.SaveChangesAsync(ct);
        return new(q.Id, q.OrderNo, q.Content, q.Rubric != null);
    }

    public async Task DeleteAsync(Guid sid, Guid id, CancellationToken ct)
    {
        var session = await Editable(sid, ct);
        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == id && x.ExamSessionId == sid, ct) ??
            throw new NotFoundException("question_not_found", "Question not found.");
        var description = $"Question in {session.Title}";
        db.Questions.Remove(q);
        CourseService.AddAudit(db, me.Id, AuditActions.Delete, AuditEntities.Question, id, description);
        await db.SaveChangesAsync(ct);
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
