using AiOralExam.Modules.AccessConfig.Application.RubricTemplates;
using AiOralExam.Modules.AccessConfig.Application.RubricTemplates;
using AiOralExam.Modules.AccessConfig.Application.RubricVersions;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

[ApiController]
[Route("api/rubric-templates")]
[Tags("Rubric Templates")]
[Authorize(Roles = Roles.LecturerOrAdmin)]
public sealed class RubricTemplatesController(
    IRubricTemplateService templates,
    IRubricVersionService versions) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<RubricTemplateDto>> Create(CreateRubricTemplateRequest request, CancellationToken ct)
    {
        var template = await templates.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = template.Id }, template);
    }

    [HttpGet]
    public Task<IReadOnlyList<RubricTemplateDto>> List([FromQuery] Guid? courseId, CancellationToken ct) =>
        templates.ListAsync(courseId, ct);

    [HttpGet("{id:guid}")]
    public Task<RubricTemplateDto> Get(Guid id, CancellationToken ct) =>
        templates.GetAsync(id, ct);

    [HttpPatch("{id:guid}")]
    public Task<RubricTemplateDto> Update(Guid id, UpdateRubricTemplateRequest request, CancellationToken ct) =>
        templates.UpdateAsync(id, request, ct);

    [HttpPost("{templateId:guid}/versions")]
    public async Task<ActionResult<RubricVersionDetailDto>> CreateVersion(
        Guid templateId, CreateRubricVersionRequest request, CancellationToken ct)
    {
        var version = await versions.CreateAsync(templateId, request, ct);
        return CreatedAtAction(nameof(GetVersion), new { templateId, versionId = version.Id }, version);
    }

    [HttpGet("{templateId:guid}/versions")]
    public Task<IReadOnlyList<RubricVersionDto>> ListVersions(Guid templateId, CancellationToken ct) =>
        versions.ListAsync(templateId, ct);

    [HttpGet("{templateId:guid}/versions/{versionId:guid}")]
    public Task<RubricVersionDetailDto> GetVersion(Guid templateId, Guid versionId, CancellationToken ct) =>
        versions.GetAsync(templateId, versionId, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await templates.DeleteAsync(id, ct);
        return NoContent();
    }
}
