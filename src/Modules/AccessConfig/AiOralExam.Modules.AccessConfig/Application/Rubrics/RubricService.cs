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

namespace AiOralExam.Modules.AccessConfig.Application.Rubrics;
public sealed record RubricCriterionDto(Guid Id, int OrderNo, string Description, decimal MaxPoints);

public sealed record RubricDto(Guid Id, decimal MaxScore, IReadOnlyList<RubricCriterionDto> Criteria);

public sealed record SaveRubricCriterionRequest(
    Guid? Id,
    [Range(1, int.MaxValue)] int OrderNo,
    [Required] string Description,
    [Range(0, 999.99)] decimal MaxPoints);

/// <summary>The criteria list replaces the existing list; omitted criterion IDs are removed.</summary>
public sealed record SaveRubricRequest([Required] IReadOnlyList<SaveRubricCriterionRequest> Criteria);

public interface IRubricService
{
    Task<RubricDto> GetAsync(Guid sid, Guid qid, CancellationToken ct);
    Task<RubricDto> SaveAsync(Guid sid, Guid qid, SaveRubricRequest r, CancellationToken ct);
    Task DeleteAsync(Guid sid, Guid qid, CancellationToken ct);
}

internal sealed class RubricService(AccessConfigDbContext db, ICurrentUser me) : IRubricService
{
    private const decimal MaxRubricScore = 999.99m;

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
            .Select(x => new RubricDto(
                x.Rubric.Id,
                x.Rubric.MaxScore,
                x.Rubric.Criteria.OrderBy(c => c.OrderNo)
                    .Select(c => new RubricCriterionDto(c.Id, c.OrderNo, c.Description, c.MaxPoints)).ToList()))
            .FirstOrDefaultAsync(ct) ??
            throw new NotFoundException("rubric_not_found", "Rubric not found.");
        return r;
    }

    public async Task<RubricDto> SaveAsync(Guid sid, Guid qid, SaveRubricRequest r, CancellationToken ct)
    {
        Validate(r);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Editable(sid, ct);
        var q = await db.Questions.Include(x => x.Rubric).ThenInclude(x => x!.Criteria)
            .FirstOrDefaultAsync(x => x.Id == qid && x.ExamSessionId == sid, ct) ??
            throw new NotFoundException("question_not_found", "Question not found.");

        var rubric = q.Rubric;
        var isNew = rubric is null;
        if (rubric is null)
        {
            rubric = new Rubric
            {
                Id = Guid.NewGuid(),
                QuestionId = q.Id
            };
            q.Rubric = rubric;
            db.Rubrics.Add(rubric);
        }

        var existing = rubric.Criteria.ToDictionary(x => x.Id);
        var retainedIds = r.Criteria.Where(x => x.Id.HasValue).Select(x => x.Id!.Value).ToHashSet();
        if (retainedIds.Any(id => !existing.ContainsKey(id)))
            throw InvalidCriteria("A criterion ID must belong to this rubric.");

        var removed = rubric.Criteria.Where(x => !retainedIds.Contains(x.Id)).ToList();
        db.RubricCriteria.RemoveRange(removed);
        foreach (var criterion in removed)
            rubric.Criteria.Remove(criterion);

        foreach (var item in r.Criteria)
        {
            RubricCriterion criterion;
            if (item.Id is Guid id)
            {
                criterion = existing[id];
            }
            else
            {
                criterion = new RubricCriterion
                {
                    Id = Guid.NewGuid(),
                    RubricId = rubric.Id,
                    Rubric = rubric
                };
                rubric.Criteria.Add(criterion);
                db.RubricCriteria.Add(criterion);
            }

            criterion.OrderNo = item.OrderNo;
            criterion.Description = item.Description.Trim();
            criterion.MaxPoints = item.MaxPoints;
        }

        CourseService.AddAudit(db, me.Id, isNew ? AuditActions.Create : AuditActions.Update,
            AuditEntities.Rubric, rubric.Id, $"Rubric for question {q.Id}");
        await db.SaveChangesAsync(ct);
        await db.Entry(rubric).ReloadAsync(ct);
        await transaction.CommitAsync(ct);
        return ToDto(rubric);
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
        var description = $"Rubric for question {qid}";
        db.Rubrics.Remove(r);
        CourseService.AddAudit(db, me.Id, AuditActions.Delete, AuditEntities.Rubric, r.Id, description);
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(SaveRubricRequest request)
    {
        if (request.Criteria is null || request.Criteria.Count == 0)
            throw InvalidCriteria("A rubric must contain at least one criterion.");
        if (request.Criteria.Any(x => string.IsNullOrWhiteSpace(x.Description)))
            throw InvalidCriteria("Criterion descriptions cannot be empty.");
        if (request.Criteria.Any(x => x.OrderNo <= 0)
            || request.Criteria.Select(x => x.OrderNo).Distinct().Count() != request.Criteria.Count)
            throw InvalidCriteria("Criterion order numbers must be positive and unique.");
        if (request.Criteria.Any(x => x.MaxPoints < 0 || x.MaxPoints > MaxRubricScore))
            throw InvalidCriteria("Criterion max points must be between 0 and 999.99.");
        if (request.Criteria.Sum(x => x.MaxPoints) <= 0
            || request.Criteria.Sum(x => x.MaxPoints) > MaxRubricScore)
            throw InvalidCriteria("The total max points must be greater than 0 and no more than 999.99.");
        if (request.Criteria.Where(x => x.Id.HasValue).Select(x => x.Id).Distinct().Count()
            != request.Criteria.Count(x => x.Id.HasValue))
            throw InvalidCriteria("A criterion cannot be included more than once.");
        if (request.Criteria.Any(x => x.Id == Guid.Empty))
            throw InvalidCriteria("Criterion IDs cannot be empty.");
    }

    private static ValidationException InvalidCriteria(string message) =>
        new(new Dictionary<string, string[]> { ["criteria"] = [message] });

    private static RubricDto ToDto(Rubric rubric) => new(
        rubric.Id,
        rubric.MaxScore,
        rubric.Criteria.OrderBy(x => x.OrderNo)
            .Select(x => new RubricCriterionDto(x.Id, x.OrderNo, x.Description, x.MaxPoints))
            .ToList());
}
