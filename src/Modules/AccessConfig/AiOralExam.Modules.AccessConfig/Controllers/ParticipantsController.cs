using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.Participants;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;
[ApiController, Route("api/exam-sessions/{sessionId:guid}/participants"),
    Authorize(Roles = Roles.LecturerOrAdmin),
    Tags("Participants")]
public sealed class ParticipantsController(IParticipantService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ParticipantDto>> List(Guid sessionId, CancellationToken ct)
        => service.ListAsync(sessionId, ct);

    [HttpPost("import")]
    public Task<ImportStudentsResult> Import(Guid sessionId, ImportStudentsRequest r, CancellationToken ct)
        => service.ImportAsync(sessionId, r, ct);

    [HttpDelete("{participantId:guid}")]
    public async Task<IActionResult> Remove(Guid sessionId, Guid participantId, CancellationToken ct)
    {
        await service.RemoveAsync(sessionId, participantId, ct);
        return NoContent();
    }
}
