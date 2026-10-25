namespace AiOralExam.Modules.Interview.Contracts;

public sealed record ScoreReviewResult(
    Guid ReviewerId,
    decimal FinalScore,
    string? Comment,
    ReviewStatus Status,
    DateTime? ReviewedAt);
