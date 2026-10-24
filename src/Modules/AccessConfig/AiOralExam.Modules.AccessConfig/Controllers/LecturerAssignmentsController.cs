using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.LecturerAssignments;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;
[ApiController,
    Route("api/courses/{courseId:guid}/lecturers"),
    Authorize(Roles = Roles.Administrator),
    Tags("Course Lecturers")]
public sealed class LecturerAssignmentsController(ILecturerAssignmentService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<LecturerDto>> List(Guid courseId, CancellationToken ct)
        => service.ListAsync(courseId, ct);

    [HttpPost("{lecturerId:guid}")]
    public async Task<ActionResult<LecturerDto>> Assign(Guid courseId, Guid lecturerId, CancellationToken ct)
        => Ok(await service.AssignAsync(courseId, lecturerId, ct));

    [HttpDelete("{lecturerId:guid}")]
    public async Task<IActionResult> Remove(Guid courseId, Guid lecturerId, CancellationToken ct)
    {
        await service.RemoveAsync(courseId, lecturerId, ct);
        return NoContent();
    }
}
