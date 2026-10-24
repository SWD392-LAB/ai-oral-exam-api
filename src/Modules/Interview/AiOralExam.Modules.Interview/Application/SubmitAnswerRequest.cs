using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.Interview.Application;

public sealed record SubmitAnswerRequest(
    // Null/empty = no answer (e.g. the FE auto-submits when time runs out).
    [MaxLength(10000)] string? AnswerText);
