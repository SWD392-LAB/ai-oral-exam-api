using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

public sealed record CreateExamSessionRequest(
    Guid CourseId,
    [Required] string Title,
    DateTime StartTime,
    DateTime EndTime,
    [Range(1, 3600)] int TimeLimitPerAnswerSeconds,
    [Range(0, 20)] int MaxFollowUps = 2);
