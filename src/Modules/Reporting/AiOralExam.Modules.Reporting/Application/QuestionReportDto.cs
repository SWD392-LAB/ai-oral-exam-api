namespace AiOralExam.Modules.Reporting.Application;

public sealed record QuestionReportDto(
    int OrderNo,
    string Content,
    decimal MaxScore,
    decimal? SuggestedScore,
    string? AiFeedback,
    int FollowUpCount,
    IReadOnlyList<TurnReportDto> Turns);
