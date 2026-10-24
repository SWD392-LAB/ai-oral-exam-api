using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Questions;

public sealed record ImportQuestionsRequest([Required] IReadOnlyList<CreateQuestionRequest> Questions);
