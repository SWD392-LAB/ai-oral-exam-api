using AiOralExam.Modules.AccessConfig.Application.QuestionBank;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

[ApiController]
[Route("api/courses/{courseId:guid}/question-bank")]
[Authorize(Roles = Roles.LecturerOrAdmin)]
[Tags("Question bank")]
public sealed class QuestionBankController(IQuestionBankService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<QuestionBankItemDto>> List(Guid courseId, QuestionBankStatusFilter? status, CancellationToken ct) =>
        service.ListAsync(courseId, status, ct);

    [HttpGet("{itemId:guid}")]
    public Task<QuestionBankItemDto> Get(Guid courseId, Guid itemId, CancellationToken ct) =>
        service.GetAsync(courseId, itemId, ct);

    [HttpPost]
    public async Task<ActionResult<QuestionBankItemDto>> Create(Guid courseId, CreateQuestionBankItemRequest request, CancellationToken ct)
    {
        var result = await service.CreateAsync(courseId, request, ct);
        return CreatedAtAction(nameof(Get), new { courseId, itemId = result.Id }, result);
    }

    [HttpPost("import")]
    public Task<IReadOnlyList<QuestionBankItemDto>> Import(Guid courseId, ImportQuestionBankRequest request, CancellationToken ct) =>
        service.ImportAsync(courseId, request, ct);

    [HttpPut("{itemId:guid}")]
    public Task<QuestionBankItemDto> Update(Guid courseId, Guid itemId, UpdateQuestionBankItemRequest request, CancellationToken ct) =>
        service.UpdateDraftAsync(courseId, itemId, request, ct);

    [HttpPost("{itemId:guid}/approve")]
    public Task<QuestionBankItemDto> Approve(Guid courseId, Guid itemId, CancellationToken ct) =>
        service.ApproveAsync(courseId, itemId, ct);

    [HttpPost("{itemId:guid}/reject")]
    public Task<QuestionBankItemDto> Reject(Guid courseId, Guid itemId, CancellationToken ct) =>
        service.RejectAsync(courseId, itemId, ct);
}
