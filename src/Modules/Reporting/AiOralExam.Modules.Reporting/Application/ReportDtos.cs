namespace AiOralExam.Modules.Reporting.Application;

public sealed record TurnReportDto(
    int TurnNo,
    string Type,
    string QuestionText,
    string? AnswerTranscript,
    DateTime AskedAt,
    DateTime? AnsweredAt);
