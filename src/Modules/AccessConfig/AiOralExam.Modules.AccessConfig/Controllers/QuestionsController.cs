using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.Questions;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;
[ApiController,
    Route("api/exam-sessions/{sessionId:guid}/questions"),
    Authorize(Roles = Roles.LecturerOrAdmin),
    Tags("Questions")]
public sealed class QuestionsController(IQuestionService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<QuestionDto>> List(Guid sessionId, CancellationToken ct)
        => service.ListAsync(sessionId, ct);

    [HttpGet("{id:guid}")]
    public Task<QuestionDto> Get(Guid sessionId, Guid id, CancellationToken ct)
        => service.GetAsync(sessionId, id, ct);

    [HttpPost]
    public async Task<ActionResult<QuestionDto>> Create(Guid sessionId, CreateQuestionRequest r, CancellationToken ct)
    {
        var x = await service.CreateAsync(sessionId, r, ct);
        return CreatedAtAction(nameof(Get), new
        {
            sessionId,
            id = x.Id
        }, x);
    }

    [HttpPut("{id:guid}")]
    public Task<QuestionDto> Update(Guid sessionId, Guid id, UpdateQuestionRequest r, CancellationToken ct)
        => service.UpdateAsync(sessionId, id, r, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid sessionId, Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(sessionId, id, ct);
        return NoContent();
    }
}
