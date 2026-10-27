using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
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
            .Include(s => s.RubricVersion).ThenInclude(v => v!.Criteria)
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
        GetQuestions(s));

    internal static IReadOnlyList<QuestionInfo> GetQuestions(ExamSession session)
    {
        ValidateRubricConfiguration(session);
        return session.Questions
            .OrderBy(q => q.OrderNo)
            .Select(q => ToQuestionInfo(session, q))
            .ToList();
    }

    internal static void ValidateRubricConfiguration(ExamSession session)
    {
        if (session.RubricMode == RubricMode.SharedVersion)
            _ = ResolveSharedVersion(session);
        else if (session.RubricMode != RubricMode.Legacy)
            throw InvalidSharedRubric("The rubric mode is not recognized.");
    }

    internal static QuestionInfo ToQuestionInfo(ExamSession session, Question question)
    {
        var (criteria, maxScore) = ResolveRubric(session, question);
        return new QuestionInfo(question.Id, question.OrderNo, question.Content, criteria, maxScore);
    }

    internal static IReadOnlyList<RubricCriterionInfo> ToCriteria(Rubric? rubric) =>
        rubric is null
            ? []
            : rubric.Criteria.OrderBy(c => c.OrderNo)
                .Select(c => new RubricCriterionInfo(c.Id, c.OrderNo, c.Description, c.MaxPoints))
                .ToList();

    private static (IReadOnlyList<RubricCriterionInfo> Criteria, decimal MaxScore) ResolveRubric(
        ExamSession session, Question question)
    {
        if (session.RubricMode == RubricMode.Legacy)
            return (ToCriteria(question.Rubric), question.Rubric?.MaxScore ?? 0);

        return ResolveSharedVersion(session);
    }

    private static (IReadOnlyList<RubricCriterionInfo> Criteria, decimal MaxScore) ResolveSharedVersion(
        ExamSession session)
    {
        if (session.RubricVersionId is not Guid versionId
            || session.RubricVersion is not { } version
            || version.Id != versionId
            || version.CourseId is not null && version.CourseId != session.CourseId)
            throw InvalidSharedRubric("The pinned rubric version does not match the session configuration.");

        if (!version.IsSealed)
            throw InvalidSharedRubric("The selected rubric version is not sealed.");

        var criteria = version.Criteria.OrderBy(c => c.OrderNo).ToList();
        if (criteria.Count == 0
            || criteria.Any(c => c.Id == Guid.Empty
                || c.RubricVersionId != version.Id
                || c.RubricId is not null
                || c.OrderNo <= 0
                || string.IsNullOrWhiteSpace(c.Description)
                || c.MaxPoints <= 0)
            || criteria.Select(c => c.Id).Distinct().Count() != criteria.Count
            || criteria.Select(c => c.OrderNo).Distinct().Count() != criteria.Count)
            throw InvalidSharedRubric("The selected rubric version has invalid criteria.");

        var maxScore = criteria.Sum(c => c.MaxPoints);
        if (version.MaxScore <= 0 || version.MaxScore != maxScore)
            throw InvalidSharedRubric("The selected rubric version score does not match its criteria.");

        return (criteria.Select(c => new RubricCriterionInfo(c.Id, c.OrderNo, c.Description, c.MaxPoints)).ToList(),
            version.MaxScore);
    }

    private static ConflictException InvalidSharedRubric(string message) =>
        new("exam_session_rubric_invalid", $"Shared rubric configuration is invalid. {message}");
}
