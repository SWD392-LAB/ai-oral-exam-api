using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.Interview.Application;

public sealed record StartAttemptRequest([Required] Guid ExamSessionId);

public sealed record SubmitAnswerRequest(
    // Null/empty = no answer (e.g. the FE auto-submits when time runs out).
    [MaxLength(10000)] string? AnswerText);

public sealed record SubmitScoreReviewRequest(
    [Range(typeof(decimal), "0", "999.99")] decimal FinalScore,
    string? Comment);
