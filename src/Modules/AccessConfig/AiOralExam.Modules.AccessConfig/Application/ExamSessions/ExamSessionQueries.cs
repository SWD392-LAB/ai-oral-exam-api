using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

/// <summary>
/// Read exam sessions for lecturers / admins.
/// TODO (M4): CreateSession, ImportQuestions, DefineRubric, Publish (locks questions + rubrics), write audit log.
/// </summary>
public interface IExamSessionQueries
{
    Task<IReadOnlyList<CourseDto>> GetMyCoursesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExamSessionSummaryDto>> GetSessionsAsync(Guid? courseId, CancellationToken ct = default);
    Task<ExamSessionDetailDto> GetSessionAsync(Guid id, CancellationToken ct = default);
}

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

    public async Task<IReadOnlyList<ExamSessionSummaryDto>> GetSessionsAsync(Guid? courseId, CancellationToken ct = default)
    {
        var q = db.ExamSessions.AsNoTracking();
        if (!IsAdmin)
        {
            var me = currentUser.Id;
            q = q.Where(s => s.Course.Lecturers.Any(l => l.LecturerId == me));
        }
        if (courseId is not null) q = q.Where(s => s.CourseId == courseId);

        var sessions = await q
            .OrderByDescending(s => s.StartTime)
            .Select(s => new ExamSessionSummaryDto(
                s.Id, s.Course.Code, s.Title, s.StartTime, s.EndTime,
                s.TimeLimitPerAnswerSeconds, s.MaxFollowUps, s.Status.ToString(), s.Questions.Count,
                0, 0, 0))
            .ToListAsync(ct);
        if (sessions.Count == 0)
            return sessions;

        var sessionIds = sessions.Select(s => s.Id).ToArray();
        var participantCounts = await db.Participants.AsNoTracking()
            .Where(p => sessionIds.Contains(p.ExamSessionId))
            .GroupBy(p => p.ExamSessionId)
            .Select(g => new { ExamSessionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ExamSessionId, x => x.Count, ct);
        var attemptCounts = await interviewResults.GetAttemptCountsAsync(sessionIds, ct);
        var attemptCountsBySession = attemptCounts.ToDictionary(x => x.ExamSessionId);

        return sessions.Select(session =>
        {
            attemptCountsBySession.TryGetValue(session.Id, out var counts);
            return session with
            {
                ParticipantCount = participantCounts.GetValueOrDefault(session.Id),
                TakenCount = counts?.TakenCount ?? 0,
                ToReviewCount = counts?.ToReviewCount ?? 0
            };
        }).ToList();
    }

    public async Task<ExamSessionDetailDto> GetSessionAsync(Guid id, CancellationToken ct = default)
    {
        var s = await db.ExamSessions.AsNoTracking()
                    .Include(x => x.Course).ThenInclude(c => c.Lecturers)
                    .Include(x => x.Questions).ThenInclude(q => q.Rubric!).ThenInclude(r => r.Criteria)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");

        if (!IsAdmin && s.Course.Lecturers.All(l => l.LecturerId != currentUser.Id))
            throw new ForbiddenException("You are not assigned to the course of this exam session.");

        return new ExamSessionDetailDto(
            s.Id, s.CourseId, s.Course.Code, s.Course.Name, s.Title, s.StartTime, s.EndTime,
            s.TimeLimitPerAnswerSeconds, s.MaxFollowUps, s.Status.ToString(),
            s.Questions.OrderBy(q => q.OrderNo)
                .Select(q => new QuestionDto(q.Id, q.OrderNo, q.Content,
                    ExamConfigurationApi.ToCriteria(q.Rubric), q.Rubric?.MaxScore ?? 0))
                .ToList());
    }
}
