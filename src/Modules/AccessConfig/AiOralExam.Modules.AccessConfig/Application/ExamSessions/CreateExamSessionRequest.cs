using System.ComponentModel.DataAnnotations;
using AiOralExam.Modules.AccessConfig.Domain;

namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

public sealed record CreateExamSessionRequest(
    Guid CourseId,
    [Required] string Title,
    DateTime StartTime,
    DateTime EndTime,
    [Range(1, 3600)] int TimeLimitPerAnswerSeconds,
    [Range(0, 20)] int MaxFollowUps = 2,
    RubricMode? RubricMode = null,
    Guid? RubricVersionId = null);
