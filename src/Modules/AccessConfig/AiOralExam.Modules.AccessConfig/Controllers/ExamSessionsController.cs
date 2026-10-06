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
    /// <summary>Mon hoc giang vien duoc phan cong (admin: tat ca).</summary>
    [HttpGet("courses/mine")]
    public Task<IReadOnlyList<CourseDto>> MyCourses(CancellationToken ct) =>
        queries.GetMyCoursesAsync(ct);

    /// <summary>Danh sach phien thi trong cac mon cua giang vien.</summary>
    [HttpGet("exam-sessions")]
    public Task<IReadOnlyList<ExamSessionSummaryDto>> List([FromQuery] Guid? courseId, CancellationToken ct) =>
        queries.GetSessionsAsync(courseId, ct);

    /// <summary>Chi tiet phien thi kem cau hoi va rubric.</summary>
    [HttpGet("exam-sessions/{id:guid}")]
    public Task<ExamSessionDetailDto> Get(Guid id, CancellationToken ct) =>
        queries.GetSessionAsync(id, ct);
}
