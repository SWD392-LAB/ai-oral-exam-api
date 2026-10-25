namespace AiOralExam.Modules.AccessConfig.Application.Questions;

public sealed record QuestionDto(Guid Id, int OrderNo, string Content, bool HasRubric);
