using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.Interview.Application;

public sealed record StartAttemptRequest([property: Required] Guid ExamSessionId);

/// <summary>Cau hoi dang cho sinh vien tra loi (chinh hoac xoay).</summary>
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
    // Null/rong = khong tra loi (vd. FE tu nop khi het gio).
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
    // Null khi Outcome = FollowUp hoac khi an diem AI voi sinh vien.
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
