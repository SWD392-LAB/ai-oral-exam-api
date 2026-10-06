using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.Interview.Application;

public sealed record StartAttemptRequest([property: Required] Guid ExamSessionId);

/// <summary>The question waiting for the student's answer (main or follow-up).</summary>
public sealed record CurrentTurnDto(
    Guid QuestionResponseId,
    Guid QuestionId,
    int QuestionOrderNo,
    int TurnNo,
    string Type,
    string QuestionText,
    DateTime AskedAt,
    DateTime DeadlineAt);

public sealed record AttemptStateDto(
    Guid AttemptId,
    Guid ExamSessionId,
    string ExamTitle,
    string Status,
    int TimeLimitSeconds,
    int MaxFollowUps,
    int TotalQuestions,
    int AnsweredQuestions,
    DateTime StartedAt,
    DateTime? CompletedAt,
    CurrentTurnDto? CurrentTurn);

public sealed record SubmitAnswerRequest(
    // Null/empty = no answer (e.g. the FE auto-submits when time runs out).
    [property: MaxLength(10000)] string? AnswerText);

public sealed record EvaluationDto(
    Guid QuestionId,
    decimal SuggestedScore,
    decimal MaxScore,
    string Feedback);

public static class SubmitOutcome
{
    public const string FollowUp = "FollowUp";
    public const string NextQuestion = "NextQuestion";
    public const string Completed = "Completed";
}

public sealed record SubmitAnswerResponse(
    string Outcome,
    bool TimedOut,
    // Null when Outcome = FollowUp or when AI scores are hidden from students.
    EvaluationDto? Evaluation,
    AttemptStateDto Attempt);

public sealed record MySessionDto(
    Guid ExamSessionId,
    string CourseCode,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    string SessionStatus,
    bool IsOpenNow,
    int QuestionCount,
    int TimeLimitPerQuestion,
    string ParticipantStatus,
    Guid? LatestAttemptId,
    string? LatestAttemptStatus);
