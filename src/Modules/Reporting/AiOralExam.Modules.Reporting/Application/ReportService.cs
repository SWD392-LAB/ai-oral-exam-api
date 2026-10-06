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
/// Bao cao chi doc diem DA LUU (AiEvaluation, ScoreReview) - khong goi lai AI (quy tac #5).
/// Sinh vien chi xem duoc bao cao khi giang vien da chot diem.
/// </summary>
internal sealed class ReportService(
    IInterviewResultsApi results,
    IExamConfigurationApi examConfig,
    ICurrentUser currentUser) : IReportService
{
    public async Task<AttemptReportDto> GetAttemptReportAsync(Guid attemptId, CancellationToken ct = default)
    {
        var attempt = await results.GetAttemptResultAsync(attemptId, ct)
                      ?? throw new NotFoundException("attempt_not_found", "Không tìm thấy lượt thi.");

        if (currentUser.IsInRole(Roles.Student))
        {
            if (attempt.StudentId != currentUser.Id)
                throw new NotFoundException("attempt_not_found", "Không tìm thấy lượt thi.");
            if (ReportCalculator.FinalScore(attempt) is null)
                throw new ConflictException("score_not_finalized", "Giảng viên chưa chốt điểm, bạn chưa thể xem báo cáo.");
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
            student?.FullName ?? "(không rõ)", student?.StudentCode,
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
                    a.AttemptId, a.StudentId, u?.StudentCode, u?.FullName ?? "(không rõ)", a.Status.ToString(),
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

    /// <summary>TODO (M3): doi sang mau bang diem cua truong khi co file mau (open point).</summary>
    public async Task<(byte[] Content, string FileName)> ExportGradeSheetCsvAsync(Guid examSessionId, CancellationToken ct = default)
    {
        var session = await GetSessionAsync(examSessionId, ct);
        var rows = await GetSessionResultsAsync(examSessionId, ct);

        var sb = new StringBuilder();
        sb.AppendLine("STT,MSSV,Ho ten,Trang thai,Diem AI goi y,Diem chot,Thang diem");
        var i = 1;
        foreach (var r in rows)
            sb.AppendLine(string.Join(',',
                i++, r.StudentCode, Csv(r.StudentName), r.Status,
                r.SuggestedTotalScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.FinalScore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                r.MaxTotalScore.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // BOM de Excel doc dung tieng Viet
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return (bytes, $"bang-diem-{session.CourseCode}-{session.Id.ToString()[..8]}.csv");
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    private async Task<ExamSessionInfo> GetSessionAsync(Guid id, CancellationToken ct) =>
        await examConfig.GetSessionAsync(id, ct)
        ?? throw new NotFoundException("exam_session_not_found", "Không tìm thấy phiên thi.");

    private async Task EnsureCanViewSessionAsync(Guid examSessionId, CancellationToken ct)
    {
        if (currentUser.IsInRole(Roles.Administrator)) return;
        if (currentUser.IsInRole(Roles.Lecturer) &&
            await examConfig.IsLecturerOfSessionAsync(currentUser.Id, examSessionId, ct)) return;
        throw new ForbiddenException("Bạn không được phân công vào môn của phiên thi này.");
    }
}
