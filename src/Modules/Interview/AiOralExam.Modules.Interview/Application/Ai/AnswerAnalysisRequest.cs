using AiOralExam.Modules.AccessConfig.Contracts;

namespace AiOralExam.Modules.Interview.Application.Ai;

public sealed record AnswerAnalysisRequest(
    string MainQuestion,
    IReadOnlyList<RubricCriterionInfo> Criteria,
    IReadOnlyList<DialogueTurn> Dialogue,
    int FollowUpsUsed,
    int MaxFollowUps,
    string Language);
