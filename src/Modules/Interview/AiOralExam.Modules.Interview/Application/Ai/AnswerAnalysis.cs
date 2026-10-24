namespace AiOralExam.Modules.Interview.Application.Ai;

public sealed record AnswerAnalysis(
    bool IsSufficient,
    IReadOnlyList<string> MissingPoints,
    bool NeedsFollowUp,
    string? FollowUpQuestion);
