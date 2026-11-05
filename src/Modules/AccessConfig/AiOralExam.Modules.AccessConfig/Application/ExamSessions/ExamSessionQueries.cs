using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Pagination;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

internal sealed class ExamSessionQueries(
    AccessConfigDbContext db,
    ICurrentUser currentUser,
    IInterviewResultsApi interviewResults) : IExamSessionQueries
{
    private bool IsAdmin => currentUser.IsInRole(Roles.Administrator);

    public async Task<IReadOnlyList<CourseDto>> GetMyCoursesAsync(CancellationToken ct = default)
    {
        var q = db.Courses.AsNoTracking();
        if (!IsAdmin)
        {
            var me = currentUser.Id;
            q = q.Where(c => c.Lecturers.Any(l => l.LecturerId == me));
        }
        return await q.OrderBy(c => c.Code).Select(c => new CourseDto(c.Id, c.Code, c.Name)).ToListAsync(ct);
    }

    public async Task<PagedResult<ExamSessionSummaryDto>> GetSessionsAsync(Guid? courseId, int page, int pageSize, CancellationToken ct = default)
    {
        var offset = ValidatePagination(page, pageSize);

        var q = db.ExamSessions.AsNoTracking();

        // Preserve Lecturer/Admin access control.
        if (!IsAdmin)
        {
            var me = currentUser.Id;
            q = q.Where(s =>
                s.Course.Lecturers.Any(l => l.LecturerId == me));
        }

        // Preserve course filter.
        if (courseId is not null)
            q = q.Where(s => s.CourseId == courseId);

        // Count after applying access control and filters.
        var totalCount = await q.CountAsync(ct);

        // Fetch only sessions belonging to the requested page.
        var sessions = await q
            .OrderByDescending(s => s.StartTime)
            .ThenBy(s => s.Id)
            .Skip(offset)
            .Take(pageSize)
            .Select(s => new ExamSessionSummaryDto(
                s.Id,
                s.Course.Code,
                s.Title,
                s.StartTime,
                s.EndTime,
                s.TimeLimitPerAnswerSeconds,
                s.MaxFollowUps,
                s.Status.ToString(),
                s.Questions.Count,
                0, 0, 0, 0))
            .ToListAsync(ct);

        if (sessions.Count == 0)
        {
            return new PagedResult<ExamSessionSummaryDto>(
                sessions, page, pageSize, totalCount);
        }

        // Calculate statistics only for sessions on this page.
        var sessionIds = sessions.Select(s => s.Id).ToArray();

        var participantCounts = await db.Participants.AsNoTracking()
            .Where(p => sessionIds.Contains(p.ExamSessionId))
            .GroupBy(p => p.ExamSessionId)
            .Select(g => new
            {
                ExamSessionId = g.Key,
                Count = g.Count()
            })
            .ToDictionaryAsync(
                x => x.ExamSessionId,
                x => x.Count,
                ct);

        var attemptCounts =
            await interviewResults.GetAttemptCountsAsync(sessionIds, ct);

        var attemptCountsBySession =
            attemptCounts.ToDictionary(x => x.ExamSessionId);

        var items = sessions.Select(session =>
        {
            attemptCountsBySession.TryGetValue(
                session.Id, out var counts);

            return session with
            {
                ParticipantCount =
                    participantCounts.GetValueOrDefault(session.Id),

                TakenCount = counts?.TakenCount ?? 0,

                ToReviewCount = counts?.ToReviewCount ?? 0,

                InProgressCount = counts?.InProgressCount ?? 0
            };
        }).ToList();

        return new PagedResult<ExamSessionSummaryDto>(
            items, page, pageSize, totalCount);
    }

    private static int ValidatePagination(int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
            errors[nameof(page)] = ["Page must be at least 1."];
        if (pageSize < 1 || pageSize > 100)
            errors[nameof(pageSize)] = ["Page size must be between 1 and 100."];

        var offset = (long)(page - 1) * pageSize;
        if (errors.Count == 0 && offset > int.MaxValue)
            errors[nameof(page)] = ["Page is too large for the requested page size."];

        if (errors.Count > 0)
            throw new ValidationException(errors, "Invalid pagination parameters.");

        return (int)offset;
    }

    public async Task<ExamSessionDetailDto> GetSessionAsync(Guid id, CancellationToken ct = default)
    {
        var s = await db.ExamSessions.AsNoTracking()
                    .Include(x => x.Course).ThenInclude(c => c.Lecturers)
                    .Include(x => x.Questions).ThenInclude(q => q.Rubric!).ThenInclude(r => r.Criteria)
                    .Include(x => x.RubricVersion).ThenInclude(v => v!.Criteria)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");

        if (!IsAdmin && s.Course.Lecturers.All(l => l.LecturerId != currentUser.Id))
            throw new ForbiddenException("You are not assigned to the course of this exam session.");

        ExamConfigurationApi.ValidateRubricConfiguration(s);

        return new ExamSessionDetailDto(
            s.Id, s.CourseId, s.Course.Code, s.Course.Name, s.Title, s.StartTime, s.EndTime,
            s.TimeLimitPerAnswerSeconds, s.MaxFollowUps, s.Status.ToString(),
            s.Questions.OrderBy(q => q.OrderNo)
                .Select(q =>
                {
                    var question = ExamConfigurationApi.ToQuestionInfo(s, q);
                    return new QuestionDto(question.Id, question.OrderNo, question.Content,
                        question.Criteria, question.MaxScore);
                })
                .ToList(),
            s.RubricMode.ToString(), s.RubricVersionId);
    }
}
