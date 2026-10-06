using AiOralExam.Modules.Reporting.Application;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.Reporting.Controllers;

[ApiController]
[Route("api/reports")]
[Tags("Reporting")]
[Authorize]
public sealed class ReportsController(IReportService reports) : ControllerBase
{
    /// <summary>Report of one attempt. Students: only their own, and only after the score is confirmed.</summary>
    [HttpGet("attempts/{attemptId:guid}")]
    public Task<AttemptReportDto> Attempt(Guid attemptId, CancellationToken ct) =>
        reports.GetAttemptReportAsync(attemptId, ct);

    /// <summary>Per-student results of an exam session.</summary>
    [HttpGet("exam-sessions/{examSessionId:guid}/results")]
    [Authorize(Roles = Roles.LecturerOrAdmin)]
    public Task<IReadOnlyList<StudentResultDto>> Results(Guid examSessionId, CancellationToken ct) =>
        reports.GetSessionResultsAsync(examSessionId, ct);

    /// <summary>Class statistics: hardest questions, good-answer rate, score distribution.</summary>
    [HttpGet("exam-sessions/{examSessionId:guid}/statistics")]
    [Authorize(Roles = Roles.LecturerOrAdmin)]
    public Task<SessionStatisticsDto> Statistics(Guid examSessionId, CancellationToken ct) =>
        reports.GetSessionStatisticsAsync(examSessionId, ct);

    /// <summary>Grade sheet export (temporary CSV until we have the school's template).</summary>
    [HttpGet("exam-sessions/{examSessionId:guid}/grade-sheet")]
    [Authorize(Roles = Roles.LecturerOrAdmin)]
    public async Task<IActionResult> GradeSheet(Guid examSessionId, CancellationToken ct)
    {
        var (content, fileName) = await reports.ExportGradeSheetCsvAsync(examSessionId, ct);
        return File(content, "text/csv; charset=utf-8", fileName);
    }
}
