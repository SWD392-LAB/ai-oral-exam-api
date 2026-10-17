using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application;

/// <summary>Implements the public contract used by other modules (Interview, Reporting).</summary>
internal sealed class ExamConfigurationApi(AccessConfigDbContext db) : IExamConfigurationApi
{
    public async Task<ExamSessionInfo?> GetSessionAsync(Guid examSessionId, CancellationToken ct = default)
    {
        var list = await GetSessionsAsync([examSessionId], ct);
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<ExamSessionInfo>> GetSessionsAsync(IReadOnlyCollection<Guid> examSessionIds, CancellationToken ct = default)
    {
        if (examSessionIds.Count == 0) return [];

        var sessions = await db.ExamSessions
            .AsNoTracking()
            .Where(s => examSessionIds.Contains(s.Id))
            .Include(s => s.Course)
            .Include(s => s.Questions).ThenInclude(q => q.Rubric!).ThenInclude(r => r.Criteria)
            .AsSplitQuery()
            .ToListAsync(ct);

        return sessions.Select(ToInfo).ToList();
    }

    public Task<bool> IsLecturerOfSessionAsync(Guid lecturerId, Guid examSessionId, CancellationToken ct = default) =>
        db.ExamSessions.AsNoTracking()
            .Where(s => s.Id == examSessionId)
            .AnyAsync(s => s.Course.Lecturers.Any(l => l.LecturerId == lecturerId), ct);

    public async Task<IReadOnlyList<UserBasicInfo>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        return await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new UserBasicInfo(u.Id, u.FullName,
                u.Email, u.StudentCode))
            .ToListAsync(ct);
    }

    public Task<UserAccessInfo?> GetUserAccessAsync(Guid userId, CancellationToken ct = default) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserAccessInfo(u.Role.Name, u.IsActive))
            .FirstOrDefaultAsync(ct);

    internal static ExamSessionInfo ToInfo(ExamSession s) => new(
        s.Id,
        s.CourseId,
        s.Course.Code,
        s.Course.Name,
        s.Title,
        s.StartTime,
        s.EndTime,
        s.TimeLimitPerAnswerSeconds,
        s.MaxFollowUps,
        s.Status,
        s.Questions
            .OrderBy(q => q.OrderNo)
            .Select(q => new QuestionInfo(q.Id, q.OrderNo, q.Content, ToCriteria(q.Rubric), q.Rubric?.MaxScore ?? 0))
            .ToList());

    internal static IReadOnlyList<RubricCriterionInfo> ToCriteria(Rubric? rubric) =>
        rubric is null
            ? []
            : rubric.Criteria.OrderBy(c => c.OrderNo)
                .Select(c => new RubricCriterionInfo(c.Id, c.OrderNo, c.Description, c.MaxPoints))
                .ToList();
}
