using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;
[ApiController,
    Route("api/exam-sessions/{sessionId:guid}/questions/{questionId:guid}/rubric"),
    Authorize(Roles = Roles.LecturerOrAdmin),
    Tags("Rubrics")]
public sealed class RubricsController(IRubricService service) : ControllerBase
{
    [HttpGet]
    public Task<RubricDto> Get(Guid sessionId, Guid questionId, CancellationToken ct)
        => service.GetAsync(sessionId, questionId, ct);

    [HttpPut]
    public Task<RubricDto> Save(Guid sessionId, Guid questionId, SaveRubricRequest r, CancellationToken ct)
        => service.SaveAsync(sessionId, questionId, r, ct);

    [HttpDelete]
    public async Task<IActionResult> Delete(Guid sessionId, Guid questionId, CancellationToken ct)
    {
        await service.DeleteAsync(sessionId, questionId, ct);
        return NoContent();
    }
}
