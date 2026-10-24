namespace AiOralExam.Modules.Interview.Contracts;

public sealed record TurnResult(
    int TurnNo,
    TurnType Type,
    string QuestionText,
    string? AnswerTranscript,
    DateTime AskedAt,
    DateTime? AnsweredAt);
