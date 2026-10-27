using AiOralExam.Modules.AccessConfig.Application.Materials;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

[ApiController]
[Route("api/courses/{courseId:guid}/materials")]
[Authorize(Roles = Roles.LecturerOrAdmin)]
[Tags("Course materials")]
public sealed class CourseMaterialsController(IMaterialService materials) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<CourseMaterialDto>> List(Guid courseId, CancellationToken ct) => materials.ListAsync(courseId, ct);

    [HttpPost]
    [RequestSizeLimit(26 * 1024 * 1024)]
    public async Task<ActionResult<CourseMaterialDto>> Upload(Guid courseId, [FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null)
            throw new ValidationException(new Dictionary<string, string[]> { ["file"] = ["Material file is required."] });
        await using var stream = file.OpenReadStream();
        var result = await materials.UploadAsync(courseId, file.FileName, file.ContentType, stream, file.Length, ct);
        return CreatedAtAction(nameof(List), new { courseId }, result);
    }

    [HttpPost("{materialId:guid}/ingest")]
    public Task<CourseMaterialDto> Ingest(Guid courseId, Guid materialId, CancellationToken ct) => materials.IngestAsync(courseId, materialId, ct);
}
