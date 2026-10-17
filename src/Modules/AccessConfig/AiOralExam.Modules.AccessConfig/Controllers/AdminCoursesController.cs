using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

[ApiController, Route("api/admin/courses"), Authorize(Roles = Roles.Administrator)]
public sealed class AdminCoursesController(ICourseService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AdminCourseDto>> List(CancellationToken ct)
        => service.GetAllWithLecturersAsync(ct);

    [HttpPost]
    public async Task<ActionResult<CourseDto>> Create(CreateCourseRequest request, CancellationToken ct)
    {
        var course = await service.CreateAsync(request, ct);
        return Created($"/api/courses/{course.Id}", course);
    }

    [HttpPut("{id:guid}")]
    public Task<CourseDto> Update(Guid id, UpdateCourseRequest request, CancellationToken ct)
        => service.UpdateAsync(id, request, ct);
}
