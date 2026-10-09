using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;
[ApiController, Route("api/courses"),
    Authorize(Roles = Roles.Administrator),
    Tags("Courses")]
public sealed class CoursesController(ICourseService service) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public Task<IReadOnlyList<CourseDto>> List(CancellationToken ct)
        => service.GetAllAsync(ct);

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public Task<CourseDto> Get(Guid id, CancellationToken ct)
        => service.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<CourseDto>> Create(CreateCourseRequest r, CancellationToken ct)
    {
        var x = await service.CreateAsync(r, ct);
        return CreatedAtAction(nameof(Get),
            new
            {
                id = x.Id
            }, x);
    }

    [HttpPut("{id:guid}")]
    public Task<CourseDto> Update(Guid id, UpdateCourseRequest r, CancellationToken ct)
        => service.UpdateAsync(id, r, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return NoContent();
    }
}
