using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

[ApiController]
[Route("api")]
[Tags("Exam Configuration")]
[Authorize(Roles = Roles.LecturerOrAdmin)]
public sealed class ExamSessionsController(IExamSessionQueries queries) : ControllerBase
{
    /// <summary>Courses the lecturer is assigned to (admin: all courses).</summary>
    [HttpGet("courses/mine")]
    public Task<IReadOnlyList<CourseDto>> MyCourses(CancellationToken ct) =>
        queries.GetMyCoursesAsync(ct);

    /// <summary>Exam sessions in the lecturer's courses.</summary>
    [HttpGet("exam-sessions")]
    public Task<IReadOnlyList<ExamSessionSummaryDto>> List([FromQuery] Guid? courseId, CancellationToken ct) =>
        queries.GetSessionsAsync(courseId, ct);

    /// <summary>Exam session details with questions and rubrics.</summary>
    [HttpGet("exam-sessions/{id:guid}")]
    public Task<ExamSessionDetailDto> Get(Guid id, CancellationToken ct) =>
        queries.GetSessionAsync(id, ct);
}
