using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.Interview.Application;

public sealed record SubmitScoreReviewRequest(
    [Range(typeof(decimal), "0", "999.99")] decimal FinalScore,
    string? Comment);
