using AiOralExam.Modules.AccessConfig.Contracts;

namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

public sealed record ExamSessionSummaryDto(
    Guid Id,
    string CourseCode,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    int TimeLimitPerAnswerSeconds,
    int MaxFollowUps,
    string Status,
    int QuestionCount,
    int ParticipantCount,
    int TakenCount,
    int ToReviewCount,
    int InProgressCount);
