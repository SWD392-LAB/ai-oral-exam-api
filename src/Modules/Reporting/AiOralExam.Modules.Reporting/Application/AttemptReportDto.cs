namespace AiOralExam.Modules.Reporting.Application;

public sealed record AttemptReportDto(
    Guid AttemptId,
    string CourseCode,
    string ExamTitle,
    string StudentName,
    string? StudentCode,
    string Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    decimal MaxTotalScore,
    decimal? SuggestedTotalScore,
    decimal? FinalScore,
    string? LecturerComment,
    IReadOnlyList<QuestionReportDto> Questions);
