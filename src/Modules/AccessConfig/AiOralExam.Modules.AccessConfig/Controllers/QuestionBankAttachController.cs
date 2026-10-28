using AiOralExam.Modules.AccessConfig.Application.QuestionBank;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

[ApiController]
[Route("api/exam-sessions/{sessionId:guid}/questions/from-bank")]
[Authorize(Roles = Roles.LecturerOrAdmin)]
[Tags("Question bank")]
public sealed class QuestionBankAttachController(IQuestionBankService service) : ControllerBase
{
    [HttpPost]
    public Task<IReadOnlyList<Guid>> Attach(Guid sessionId, AttachQuestionBankRequest request, CancellationToken ct) =>
        service.AttachApprovedAsync(sessionId, request, ct);
}
