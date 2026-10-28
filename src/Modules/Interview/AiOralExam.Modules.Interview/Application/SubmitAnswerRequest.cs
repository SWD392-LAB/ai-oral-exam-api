using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.Interview.Application;

public sealed record SubmitAnswerRequest(
    [MaxLength(10000)] string? AnswerText,
    [Required] Guid TurnId);
