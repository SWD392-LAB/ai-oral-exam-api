namespace AiOralExam.Modules.Interview.Contracts;

public sealed record QuestionResponseResult(
    Guid QuestionResponseId,
    Guid QuestionId,
    int FollowUpCount,
    bool IsFinished,
    decimal? SuggestedScore,
    string? AiFeedback,
    IReadOnlyList<TurnResult> Turns);
