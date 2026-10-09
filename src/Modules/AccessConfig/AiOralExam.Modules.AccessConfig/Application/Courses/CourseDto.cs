using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AiOralExam.Modules.AccessConfig.Application.Courses;
public sealed record CourseDto(
    Guid Id,
    string Code,
    string Name,
    int LecturerCount,
    int ExamSessionCount);

public sealed record CreateCourseRequest(
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(200)] string Name);

public sealed record UpdateCourseRequest(
    [Required, MaxLength(200)] string Name);

