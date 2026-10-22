using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Courses;

public sealed record CreateCourseRequest(
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(200)] string Name,
    Guid LecturerId);

public sealed record UpdateCourseRequest(
    [Required, MaxLength(200)] string Name);
