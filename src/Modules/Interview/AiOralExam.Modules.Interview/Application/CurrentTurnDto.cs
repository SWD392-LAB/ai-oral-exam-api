namespace AiOralExam.Modules.Interview.Application;

/// <summary>The question waiting for the student's answer (main or follow-up).</summary>
public sealed record CurrentTurnDto(
    Guid QuestionResponseId,
    Guid QuestionId,
    int QuestionOrderNo,
    int TurnNo,
    string Type,
    string QuestionText,
    DateTime AskedAt,
    DateTime DeadlineAt,
    Guid TurnId);
