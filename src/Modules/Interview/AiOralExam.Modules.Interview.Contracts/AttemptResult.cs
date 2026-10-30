namespace AiOralExam.Modules.Interview.Contracts;

public sealed record AttemptResult(
    Guid AttemptId,
    Guid ExamSessionId,
    Guid StudentId,
    AttemptStatus Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    ScoreReviewResult? Review,
    IReadOnlyList<QuestionResponseResult> Responses)
{
    public decimal? SuggestedTotalScore => Responses.Count == 0
        || Responses.Any(response => !response.IsFinished || response.SuggestedScore is null)
        ? null
        : Responses.Sum(response => response.SuggestedScore!.Value);
}
