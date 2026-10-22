using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Questions;

public sealed record CreateQuestionRequest(
    [Range(1, int.MaxValue)] int OrderNo,
    [Required] string Content);

public sealed record ImportQuestionsRequest([Required] IReadOnlyList<CreateQuestionRequest> Questions);

public sealed record UpdateQuestionRequest(
    [Range(1, int.MaxValue)] int OrderNo,
    [Required] string Content);
