using AiOralExam.Modules.AccessConfig.Application.RubricVersions;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

[ApiController]
[Route("api/platform-rubric")]
[Authorize(Roles = Roles.LecturerOrAdmin)]
public sealed class PlatformRubricController(IRubricVersionService versions) : ControllerBase
{
    [HttpGet]
    public Task<RubricVersionDetailDto> Get(CancellationToken ct) => versions.GetActivePlatformAsync(ct);
}
