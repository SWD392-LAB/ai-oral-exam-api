using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.AIServices;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

[ApiController, Route("api/ai-services"),
                Authorize(Roles = Roles.Administrator),
                Tags("AI Service Config")]

public sealed class AiConfigsController(IAiConfigService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AiServiceConfigDto>> List(CancellationToken ct)
        => service.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<AiServiceConfigDto> Get(Guid id, CancellationToken ct)
        => service.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<AiServiceConfigDto>> Create(SaveAiServiceConfigRequest r, CancellationToken ct)
    {
        var x = await service.SaveAsync(null, r, ct);
        return CreatedAtAction(nameof(Get),
            new
            {
                id = x.Id
            }, x);
    }

    [HttpPut("{id:guid}")]
    public Task<AiServiceConfigDto> Update(Guid id, SaveAiServiceConfigRequest r, CancellationToken ct)
        => service.SaveAsync(id, r, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return NoContent();
    }
}
