namespace AiOralExam.Modules.Reporting.Application;

public sealed record QuestionStatisticDto(
    Guid QuestionId,
    int OrderNo,
    string Content,
    decimal MaxScore,
    // Confirmed attempts, including unanswered questions counted as zero.
    int AnswerCount,
    decimal AverageScore,
    // Share of answers scoring >= 70% of the maximum.
    decimal GoodAnswerRate,
    double AverageFollowUps);
