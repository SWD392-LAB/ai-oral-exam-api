using System.Text;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;

namespace AiOralExam.Modules.Reporting.Application;

public interface IReportService
{
    Task<AttemptReportDto> GetAttemptReportAsync(Guid attemptId, CancellationToken ct = default);
    Task<IReadOnlyList<StudentResultDto>> GetSessionResultsAsync(Guid examSessionId, CancellationToken ct = default);
    Task<SessionStatisticsDto> GetSessionStatisticsAsync(Guid examSessionId, CancellationToken ct = default);
    Task<(byte[] Content, string FileName)> ExportGradeSheetCsvAsync(Guid examSessionId, CancellationToken ct = default);
}

/// <summary>
/// Reports only read STORED scores (AiEvaluation, ScoreReview) - never call the AI again (rule #5).
/// Students can see a report only after the lecturer has confirmed the score.
/// </summary>
internal sealed class ReportService(
    IInterviewResultsApi results,
    IExamConfigurationApi examConfig,
    ICurrentUser currentUser) : IReportService
{
    public async Task<AttemptReportDto> GetAttemptReportAsync(Guid attemptId, CancellationToken ct = default)
    {
        var attempt = await results.GetAttemptResultAsync(attemptId, ct)
                      ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");

        if (currentUser.IsInRole(Roles.Student))
        {
            if (attempt.StudentId != currentUser.Id)
                throw new NotFoundException("attempt_not_found", "Attempt not found.");
            if (ReportCalculator.FinalScore(attempt) is null)
                throw new ConflictException("score_not_finalized", "The lecturer has not confirmed the score yet, so the report is not available.");
        }
        else
        {
            await EnsureCanViewSessionAsync(attempt.ExamSessionId, ct);
        }

        var session = await GetSessionAsync(attempt.ExamSessionId, ct);
        var student = (await examConfig.GetUsersAsync([attempt.StudentId], ct)).FirstOrDefault();

        var questions = session.Questions.Select(q =>
        {
            var r = attempt.Responses.FirstOrDefault(x => x.QuestionId == q.Id);
            return new QuestionReportDto(
                q.OrderNo, q.Content, q.MaxScore, r?.SuggestedScore, r?.AiFeedback, r?.FollowUpCount ?? 0,
                r?.Turns.Select(t => new TurnReportDto(t.TurnNo, t.Type.ToString(), t.QuestionText,
                    t.AnswerTranscript, t.AskedAt, t.AnsweredAt)).ToList() ?? []);
        }).ToList();

        return new AttemptReportDto(
            attempt.AttemptId, session.CourseCode, session.Title,
            student?.FullName ?? "(unknown)", student?.StudentCode,
            attempt.Status.ToString(), attempt.StartedAt, attempt.CompletedAt,
            session.TotalMaxScore, ReportCalculator.SuggestedTotal(attempt), ReportCalculator.FinalScore(attempt),
            attempt.Review?.Comment, questions);
    }

    public async Task<IReadOnlyList<StudentResultDto>> GetSessionResultsAsync(Guid examSessionId, CancellationToken ct = default)
    {
        await EnsureCanViewSessionAsync(examSessionId, ct);
        var session = await GetSessionAsync(examSessionId, ct);
        var attempts = await results.GetSessionResultsAsync(examSessionId, ct);
        var users = (await examConfig.GetUsersAsync(attempts.Select(a => a.StudentId).Distinct().ToList(), ct))
            .ToDictionary(u => u.Id);

        return attempts
            .Select(a =>
            {
                users.TryGetValue(a.StudentId, out var u);
                return new StudentResultDto(
                    a.AttemptId, a.StudentId, u?.StudentCode, u?.FullName ?? "(unknown)", a.Status.ToString(),
                    ReportCalculator.SuggestedTotal(a), ReportCalculator.FinalScore(a), session.TotalMaxScore,
                    a.CompletedAt);
            })
            .OrderBy(r => r.StudentCode)
            .ToList();
    }

    public async Task<SessionStatisticsDto> GetSessionStatisticsAsync(Guid examSessionId, CancellationToken ct = default)
    {
        await EnsureCanViewSessionAsync(examSessionId, ct);
        var session = await GetSessionAsync(examSessionId, ct);
        var attempts = await results.GetSessionResultsAsync(examSessionId, ct);
        return ReportCalculator.Statistics(session, attempts);
    }

    /// <summary>TODO (M3): switch to the school's grade-sheet template once we have it (open point).</summary>
    public async Task<(byte[] Content, string FileName)> ExportGradeSheetCsvAsync(Guid examSessionId, CancellationToken ct = default)
    {
        var session = await GetSessionAsync(examSessionId, ct);
        var rows = await GetSessionResultsAsync(examSessionId, ct);

        var sb = new StringBuilder();
        sb.AppendLine("No,StudentCode,FullName,Status,AiSuggestedScore,FinalScore,MaxScore");
        var i = 1;
        foreach (var r in rows)
            sb.AppendLine(string.Join(',',
                i++, r.StudentCode, Csv(r.StudentName), r.Status,
                r.SuggestedTotalScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.FinalScore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                r.MaxTotalScore.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // BOM so Excel reads UTF-8 (accented names) correctly
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return (bytes, $"grade-sheet-{session.CourseCode}-{session.Id.ToString()[..8]}.csv");
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    private async Task<ExamSessionInfo> GetSessionAsync(Guid id, CancellationToken ct) =>
        await examConfig.GetSessionAsync(id, ct)
        ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");

    private async Task EnsureCanViewSessionAsync(Guid examSessionId, CancellationToken ct)
    {
        if (currentUser.IsInRole(Roles.Administrator)) return;
        if (currentUser.IsInRole(Roles.Lecturer) &&
            await examConfig.IsLecturerOfSessionAsync(currentUser.Id, examSessionId, ct)) return;
        throw new ForbiddenException("You are not assigned to the course of this exam session.");
    }
}
