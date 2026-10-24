using AiOralExam.Modules.AccessConfig.Contracts;

namespace AiOralExam.Modules.Interview.Application.Ai;

public sealed record RubricScoringRequest(
    string MainQuestion,
    IReadOnlyList<RubricCriterionInfo> Criteria,
    IReadOnlyList<DialogueTurn> Dialogue,
    string Language);
