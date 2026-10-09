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

namespace AiOralExam.Modules.AccessConfig.Application.Rubrics;
public sealed record RubricDto(Guid Id, string Criteria, decimal MaxScore);

public sealed record SaveRubricRequest(
    [Required] string Criteria,
    [Range(0.01, 99999)] decimal MaxScore);

public interface IRubricService
{
    Task<RubricDto> GetAsync(Guid sid, Guid qid, CancellationToken ct);
    Task<RubricDto> SaveAsync(Guid sid, Guid qid, SaveRubricRequest r, CancellationToken ct);
    Task DeleteAsync(Guid sid, Guid qid, CancellationToken ct);
}

internal sealed class RubricService(AccessConfigDbContext db, ICurrentUser me) : IRubricService
{
    async Task<ExamSession> Editable(Guid sid, CancellationToken ct)
    {
        var s = await db.ExamSessions.Include(x => x.Course).ThenInclude(x => x.Lecturers)
            .FirstOrDefaultAsync(x => x.Id == sid, ct) ??
            throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        if (s.Status != ExamSessionStatus.Draft)
            throw new ConflictException("exam_session_locked", "Only draft exam sessions can be edited.");
        if (!me.IsInRole(Roles.Administrator) && s.Course.Lecturers.All(x => x.LecturerId != me.Id))
            throw new ForbiddenException("You are not assigned to this course.");
        return s;
    }

    public async Task<RubricDto> GetAsync(Guid sid, Guid qid, CancellationToken ct)
    {
        var r = await db.Rubrics.AsNoTracking()
            .Where(x => x.QuestionId == qid)
            .Join(db.Questions,
                rubric => rubric.QuestionId,
                question => question.Id,
                (rubric, question) => new { Rubric = rubric, Question = question })
            .Where(x => x.Question.ExamSessionId == sid)
            .Select(x => new RubricDto(x.Rubric.Id, x.Rubric.Criteria, x.Rubric.MaxScore))
            .FirstOrDefaultAsync(ct) ??
            throw new NotFoundException("rubric_not_found", "Rubric not found.");
        return r;
    }

    public async Task<RubricDto> SaveAsync(Guid sid, Guid qid, SaveRubricRequest r, CancellationToken ct)
    {
        await Editable(sid, ct);
        var q = await db.Questions.Include(x => x.Rubric)
            .FirstOrDefaultAsync(x => x.Id == qid && x.ExamSessionId == sid, ct) ??
            throw new NotFoundException("question_not_found", "Question not found.");
        if (q.Rubric is null)
        {
            q.Rubric = new Rubric
            {
                Id = Guid.NewGuid(),
                QuestionId = q.Id
            };
            db.Rubrics.Add(q.Rubric);
        }
        q.Rubric.Criteria = r.Criteria.Trim();
        q.Rubric.MaxScore = r.MaxScore;
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Save", "Rubric", q.Rubric.Id, ct);
        return new(q.Rubric.Id, q.Rubric.Criteria, q.Rubric.MaxScore);
    }

    public async Task DeleteAsync(Guid sid, Guid qid, CancellationToken ct)
    {
        await Editable(sid, ct);
        var r = await db.Rubrics
            .Join(db.Questions,
                rubric => rubric.QuestionId,
                question => question.Id,
                (rubric, question) => new { Rubric = rubric, Question = question })
            .Where(x => x.Rubric.QuestionId == qid && x.Question.ExamSessionId == sid)
            .Select(x => x.Rubric)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("rubric_not_found", "Rubric not found.");
        db.Rubrics.Remove(r);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Delete", "Rubric", r.Id, ct);
    }
}
