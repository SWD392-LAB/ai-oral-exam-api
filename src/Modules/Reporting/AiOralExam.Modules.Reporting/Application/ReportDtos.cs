namespace AiOralExam.Modules.Reporting.Application;

public sealed record TurnReportDto(
    int TurnNo,
    string Type,
    string QuestionText,
    string? AnswerTranscript,
    DateTime AskedAt,
    DateTime? AnsweredAt);

public sealed record QuestionReportDto(
    int OrderNo,
    string Content,
    decimal MaxScore,
    decimal? SuggestedScore,
    string? AiFeedback,
    int FollowUpCount,
    IReadOnlyList<TurnReportDto> Turns);

public sealed record AttemptReportDto(
    Guid AttemptId,
    string CourseCode,
    string ExamTitle,
    string StudentName,
    string? StudentCode,
    string Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    decimal MaxTotalScore,
    decimal SuggestedTotalScore,
    decimal? FinalScore,
    string? LecturerComment,
    IReadOnlyList<QuestionReportDto> Questions);

public sealed record StudentResultDto(
    Guid AttemptId,
    Guid StudentId,
    string? StudentCode,
    string StudentName,
    string Status,
    decimal SuggestedTotalScore,
    decimal? FinalScore,
    decimal MaxTotalScore,
    DateTime? CompletedAt);

public sealed record QuestionStatisticDto(
    Guid QuestionId,
    int OrderNo,
    string Content,
    decimal MaxScore,
    int AnswerCount,
    decimal AverageScore,
    // Share of answers scoring >= 70% of the maximum.
    decimal GoodAnswerRate,
    double AverageFollowUps);

public sealed record ScoreBucketDto(string Range, int Count);

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
