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
    /// <summary>Bao cao 1 luot thi. Sinh vien: chi luot cua minh va chi khi diem da chot.</summary>
    [HttpGet("attempts/{attemptId:guid}")]
    public Task<AttemptReportDto> Attempt(Guid attemptId, CancellationToken ct) =>
        reports.GetAttemptReportAsync(attemptId, ct);

    /// <summary>Ket qua tung sinh vien cua 1 phien thi.</summary>
    [HttpGet("exam-sessions/{examSessionId:guid}/results")]
    [Authorize(Roles = Roles.LecturerOrAdmin)]
    public Task<IReadOnlyList<StudentResultDto>> Results(Guid examSessionId, CancellationToken ct) =>
        reports.GetSessionResultsAsync(examSessionId, ct);

    /// <summary>Thong ke lop: cau kho nhat, ti le tra loi tot, phan bo diem.</summary>
    [HttpGet("exam-sessions/{examSessionId:guid}/statistics")]
    [Authorize(Roles = Roles.LecturerOrAdmin)]
    public Task<SessionStatisticsDto> Statistics(Guid examSessionId, CancellationToken ct) =>
        reports.GetSessionStatisticsAsync(examSessionId, ct);

    /// <summary>Xuat bang diem (CSV tam thoi, cho mau cua truong).</summary>
    [HttpGet("exam-sessions/{examSessionId:guid}/grade-sheet")]
    [Authorize(Roles = Roles.LecturerOrAdmin)]
    public async Task<IActionResult> GradeSheet(Guid examSessionId, CancellationToken ct)
    {
        var (content, fileName) = await reports.ExportGradeSheetCsvAsync(examSessionId, ct);
        return File(content, "text/csv; charset=utf-8", fileName);
    }
}
