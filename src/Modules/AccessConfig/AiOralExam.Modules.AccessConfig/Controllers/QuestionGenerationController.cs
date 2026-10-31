using AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;
using AiOralExam.Modules.AccessConfig.Application.QuestionBank;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

[ApiController]
[Route("api/courses/{courseId:guid}/question-bank/generations")]
[Authorize(Roles = Roles.LecturerOrAdmin)]
[ApiExplorerSettings(GroupName = "v1")]
public sealed class QuestionGenerationController(IQuestionGenerator generator, IQuestionBankService bank) : ControllerBase
{
    /// <summary>
    /// Generates reviewable Draft candidates from supplied excerpts/materials and
    /// persists them as Draft question-bank items. Approval and session attachment
    /// remain separate lecturer-controlled operations.
    /// </summary>
    [HttpPost]
    public async Task<QuestionGenerationResult> Generate(
        Guid courseId,
        QuestionGenerationRequest request,
        CancellationToken ct)
    {
        var result = await generator.GenerateAsync(courseId, request, ct);
        await bank.CreateAiDraftsAsync(courseId, request.Topic, result.Drafts, request.SourceMaterialIds, ct);
        return result;
    }
}
