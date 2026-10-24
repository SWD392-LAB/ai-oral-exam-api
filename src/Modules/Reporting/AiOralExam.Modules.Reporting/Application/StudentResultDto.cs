namespace AiOralExam.Modules.Reporting.Application;

public sealed record StudentResultDto(
    Guid AttemptId,
    Guid StudentId,
    string? StudentCode,
    string StudentName,
    string Status,
    decimal SuggestedTotalScore,
    decimal? FinalScore,
    decimal MaxTotalScore,
    DateTime? CompletedAt);
