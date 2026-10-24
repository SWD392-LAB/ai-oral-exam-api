using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Courses;

public sealed record UpdateCourseRequest(
    [Required, MaxLength(200)] string Name);
