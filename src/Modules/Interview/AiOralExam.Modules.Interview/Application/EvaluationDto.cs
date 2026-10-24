namespace AiOralExam.Modules.Interview.Application;

public sealed record EvaluationDto(
    Guid QuestionId,
    decimal SuggestedScore,
    decimal MaxScore,
    string Feedback);
