using System;
using System.Collections.Generic;

using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using ValidationException = AiOralExam.SharedKernel.Errors.ValidationException;


namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;
internal sealed class ExamSessionCommands(AccessConfigDbContext db, ICurrentUser me) : IExamSessionCommands
{
    private const decimal MaximumRubricScore = 999.99m;

    async Task<ExamSession> Load(Guid id, CancellationToken ct)
    {
        var s = await db.ExamSessions
            .Include(x => x.Course).ThenInclude(x => x.Lecturers)
            .Include(x => x.Questions).ThenInclude(x => x.Rubric).ThenInclude(x => x!.Criteria)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ??
            throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        if (!me.IsInRole(Roles.Administrator) && s.Course.Lecturers.All(x => x.LecturerId != me.Id))
            throw new ForbiddenException("You are not assigned to this course.");
        return s;
    }

    static DateTime Utc(DateTime x)
        => x.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(x, DateTimeKind.Utc) : x.ToUniversalTime();

    static ExamSessionCommandDto D(ExamSession s, int pc)
        => new(s.Id, s.CourseId, s.Title, s.StartTime, s.EndTime, s.TimeLimitPerAnswerSeconds, s.MaxFollowUps,
            s.Status.ToString(), s.Questions.Count, pc, s.RubricMode.ToString(), s.RubricVersionId);

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
        var rubricMode = r.RubricMode ?? RubricMode.Legacy;
        if (!Enum.IsDefined(typeof(RubricMode), rubricMode))
            throw InvalidRubricSelection("rubricMode", "Rubric mode must be Legacy or SharedVersion.");

        RubricVersion? selectedVersion = null;
        if (rubricMode == RubricMode.Legacy)
        {
            if (r.RubricVersionId is not null)
                throw InvalidRubricSelection("rubricVersionId", "Legacy sessions cannot select a rubric version.");
        }
        else
        {
            if (r.RubricVersionId is not Guid versionId || versionId == Guid.Empty)
                throw InvalidRubricSelection("rubricVersionId", "A rubric version is required for SharedVersion sessions.");

            selectedVersion = await db.RubricVersions.AsNoTracking()
                .Include(x => x.Template)
                .Include(x => x.Criteria)
                .FirstOrDefaultAsync(x => x.Id == versionId, ct) ??
                throw new NotFoundException("rubric_version_not_found", "Rubric version not found.");

            if (selectedVersion.CourseId != c.Id
                || selectedVersion.Template is null
                || selectedVersion.Template.Id != selectedVersion.TemplateId
                || selectedVersion.Template.CourseId != c.Id)
                throw new NotFoundException("rubric_version_not_found", "Rubric version not found.");

            ValidateSelectedRubricVersion(selectedVersion);
        }

        var s = new ExamSession
        {
            Id = Guid.NewGuid(),
            CourseId = c.Id,
            RubricMode = rubricMode,
            RubricVersionId = selectedVersion?.Id,
            CreatedBy = me.Id,
            Title = r.Title.Trim(),
            StartTime = start,
            EndTime = end,
            TimeLimitPerAnswerSeconds = r.TimeLimitPerAnswerSeconds,
            MaxFollowUps = r.MaxFollowUps,
            Status = ExamSessionStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };
        db.ExamSessions.Add(s);
        CourseService.AddAudit(db, me.Id, AuditActions.Create, AuditEntities.ExamSession, s.Id,
            $"Exam session {s.Title} in course {c.Code}");
        await db.SaveChangesAsync(ct);
        return D(s, 0);
    }

    private static ValidationException InvalidRubricSelection(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });

    private static void ValidateSelectedRubricVersion(RubricVersion version)
    {
        if (!version.IsSealed)
            throw new ConflictException("rubric_version_not_sealed", "Only sealed rubric versions can be selected.");

        var criteria = version.Criteria.OrderBy(x => x.OrderNo).ToList();
        if (criteria.Count == 0
            || criteria.Any(x => x.Id == Guid.Empty
                || x.RubricVersionId != version.Id
                || x.RubricId is not null
                || x.OrderNo <= 0
                || string.IsNullOrWhiteSpace(x.Description)
                || x.MaxPoints <= 0
                || x.MaxPoints > MaximumRubricScore
                || decimal.Round(x.MaxPoints, 2) != x.MaxPoints)
            || criteria.Select(x => x.Id).Distinct().Count() != criteria.Count
            || criteria.Select(x => x.OrderNo).Distinct().Count() != criteria.Count)
            throw new ConflictException("rubric_version_invalid", "The selected rubric version has invalid criteria.");

        var calculatedMaxScore = criteria.Sum(x => x.MaxPoints);
        if (calculatedMaxScore <= 0
            || calculatedMaxScore > MaximumRubricScore
            || version.MaxScore != calculatedMaxScore)
            throw new ConflictException("rubric_version_invalid", "The selected rubric version score does not match its criteria.");
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
        s.TimeLimitPerAnswerSeconds = r.TimeLimitPerAnswerSeconds;
        s.MaxFollowUps = r.MaxFollowUps;
        CourseService.AddAudit(db, me.Id, AuditActions.Update, AuditEntities.ExamSession, id,
            $"Exam session {s.Title} in course {s.Course.Code}");
        await db.SaveChangesAsync(ct);
        return D(s, await db.Participants.CountAsync(x => x.ExamSessionId == id, ct));
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var s = await Load(id, ct);
        if (s.Status != ExamSessionStatus.Draft)
            throw new ConflictException("exam_session_locked", "Only draft exam sessions can be deleted.");
        var description = $"Exam session {s.Title} in course {s.Course.Code}";
        db.ExamSessions.Remove(s);
        CourseService.AddAudit(db, me.Id, AuditActions.Delete, AuditEntities.ExamSession, id, description);
        await db.SaveChangesAsync(ct);
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

        if (s.RubricMode == RubricMode.Legacy)
        {
            var missing = s.Questions.Where(x => x.Rubric is null
                || x.Rubric.Criteria.Count == 0
                || x.Rubric.MaxScore <= 0)
                .Select(x => x.OrderNo)
                .OrderBy(x => x).ToList();
            if (missing.Count > 0)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    {
                        "rubric", [$"Every question needs a rubric with at least one criterion and a max score greater than 0. Missing for question(s): " +
                        $"{string.Join(", ", missing)}."]
                    }
                });
        }
        else if (s.RubricMode == RubricMode.SharedVersion)
        {
            var version = await LoadSharedVersionForPublishAsync(s, ct);
            if (s.Questions.Any(x => x.Rubric is not null))
                throw new ConflictException("exam_session_rubric_invalid",
                    "SharedVersion sessions cannot contain question-specific rubrics.");
            if (s.Questions.Count * version.MaxScore > MaximumRubricScore)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["rubric"] = ["The session maximum score cannot exceed 999.99."]
                });
        }
        else
        {
            throw new ConflictException("exam_session_rubric_invalid", "The session rubric mode is not recognized.");
        }

        var count = await db.Participants.CountAsync(x => x.ExamSessionId == id, ct);
        if (count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["participants"] = ["Add at least one student before publishing."]
            });
        s.Status = ExamSessionStatus.Published;
        CourseService.AddAudit(db, me.Id, AuditActions.Publish, AuditEntities.ExamSession, id,
            $"Exam session {s.Title} in course {s.Course.Code}");
        await db.SaveChangesAsync(ct);
        return D(s, count);
    }

    private async Task<RubricVersion> LoadSharedVersionForPublishAsync(ExamSession session, CancellationToken ct)
    {
        if (session.RubricVersionId is not Guid versionId || versionId == Guid.Empty)
            throw new ConflictException("exam_session_rubric_invalid",
                "A SharedVersion session must have a selected rubric version.");

        var version = await db.RubricVersions.AsNoTracking()
            .Include(x => x.Template)
            .Include(x => x.Criteria)
            .FirstOrDefaultAsync(x => x.Id == versionId, ct) ??
            throw new NotFoundException("rubric_version_not_found", "Rubric version not found.");

        if (version.CourseId != session.CourseId
            || version.Template is null
            || version.Template.Id != version.TemplateId
            || version.Template.CourseId != session.CourseId)
            throw new NotFoundException("rubric_version_not_found", "Rubric version not found.");

        ValidateSelectedRubricVersion(version);
        return version;
    }

    public async Task<ExamSessionCommandDto> CloseAsync(Guid id, CancellationToken ct)
    {
        var s = await Load(id, ct);
        if (s.Status != ExamSessionStatus.Published)
            throw new ConflictException("exam_session_not_published", "Only published exam sessions can be closed.");
        s.Status = ExamSessionStatus.Closed;
        CourseService.AddAudit(db, me.Id, AuditActions.Close, AuditEntities.ExamSession, id,
            $"Exam session {s.Title} in course {s.Course.Code}");
        await db.SaveChangesAsync(ct);
        return D(s, await db.Participants.CountAsync(x => x.ExamSessionId == id, ct));
    }
}
