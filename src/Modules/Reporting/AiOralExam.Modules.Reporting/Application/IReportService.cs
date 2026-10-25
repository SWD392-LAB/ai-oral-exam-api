namespace AiOralExam.Modules.Reporting.Application;

public interface IReportService
{
    Task<AttemptReportDto> GetAttemptReportAsync(Guid attemptId, CancellationToken ct = default);
    Task<IReadOnlyList<StudentResultDto>> GetSessionResultsAsync(Guid examSessionId, CancellationToken ct = default);
    Task<SessionStatisticsDto> GetSessionStatisticsAsync(Guid examSessionId, CancellationToken ct = default);
    Task<(byte[] Content, string FileName)> ExportGradeSheetCsvAsync(Guid examSessionId, CancellationToken ct = default);
}
