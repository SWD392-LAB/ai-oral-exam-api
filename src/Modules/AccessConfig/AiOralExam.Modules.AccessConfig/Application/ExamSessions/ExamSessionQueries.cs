using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

/// <summary>
/// Doc phien thi cho giang vien / admin.
/// TODO (M4): CreateSession, ImportQuestions, DefineRubric, Publish (khoa cau hoi + rubric), ghi audit log.
/// </summary>
public interface IExamSessionQueries
{
    Task<IReadOnlyList<CourseDto>> GetMyCoursesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExamSessionSummaryDto>> GetSessionsAsync(Guid? courseId, CancellationToken ct = default);
    Task<ExamSessionDetailDto> GetSessionAsync(Guid id, CancellationToken ct = default);
}

internal sealed class ExamSessionQueries(AccessConfigDbContext db, ICurrentUser currentUser) : IExamSessionQueries
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

        return await q
            .OrderByDescending(s => s.StartTime)
            .Select(s => new ExamSessionSummaryDto(
                s.Id, s.Course.Code, s.Title, s.StartTime, s.EndTime,
                s.TimeLimitPerQuestion, s.MaxFollowUps, s.Status.ToString(), s.Questions.Count))
            .ToListAsync(ct);
    }

    public async Task<ExamSessionDetailDto> GetSessionAsync(Guid id, CancellationToken ct = default)
    {
        var s = await db.ExamSessions.AsNoTracking()
                    .Include(x => x.Course).ThenInclude(c => c.Lecturers)
                    .Include(x => x.Questions).ThenInclude(q => q.Rubric)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("exam_session_not_found", "Không tìm thấy phiên thi.");

        if (!IsAdmin && s.Course.Lecturers.All(l => l.LecturerId != currentUser.Id))
            throw new ForbiddenException("Bạn không được phân công vào môn của phiên thi này.");

        return new ExamSessionDetailDto(
            s.Id, s.CourseId, s.Course.Code, s.Course.Name, s.Title, s.StartTime, s.EndTime,
            s.TimeLimitPerQuestion, s.MaxFollowUps, s.Status.ToString(),
            s.Questions.OrderBy(q => q.OrderNo)
                .Select(q => new QuestionDto(q.Id, q.OrderNo, q.Content, q.Rubric.Criteria, q.Rubric.MaxScore))
                .ToList());
    }
}
