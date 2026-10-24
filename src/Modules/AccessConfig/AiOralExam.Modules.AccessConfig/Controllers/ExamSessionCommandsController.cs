using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;
[ApiController,
    Route("api/exam-sessions"),
    Authorize(Roles = Roles.LecturerOrAdmin),
    Tags("Exam Sessions - Commands")]
public sealed class ExamSessionCommandsController(IExamSessionCommands service) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ExamSessionCommandDto>> Create(CreateExamSessionRequest r, CancellationToken ct)
    {
        var x = await service.CreateAsync(r, ct);
        return Created($"/api/exam-sessions/{x.Id}", x);
    }

    [HttpPut("{id:guid}")]
    public Task<ExamSessionCommandDto> Update(Guid id, UpdateExamSessionRequest r, CancellationToken ct)
        => service.UpdateAsync(id, r, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/publish")]
    public Task<ExamSessionCommandDto> Publish(Guid id, CancellationToken ct)
        => service.PublishAsync(id, ct);

    [HttpPost("{id:guid}/close")]
    public Task<ExamSessionCommandDto> Close(Guid id, CancellationToken ct)
        => service.CloseAsync(id, ct);
}
