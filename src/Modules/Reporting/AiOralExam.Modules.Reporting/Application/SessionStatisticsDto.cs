namespace AiOralExam.Modules.Reporting.Application;

public sealed record SessionStatisticsDto(
    Guid ExamSessionId,
    string ExamTitle,
    int CompletedAttempts,
    int FinalizedAttempts,
    int PendingReviewAttempts,
    decimal? AverageFinalScore,
    decimal MaxTotalScore,
    // Distribution of final scores (on a 10-point scale) of finalized attempts.
    IReadOnlyList<ScoreBucketDto> ScoreDistribution,
    // Sorted hardest first (lowest average score in %).
    IReadOnlyList<QuestionStatisticDto> Questions);
