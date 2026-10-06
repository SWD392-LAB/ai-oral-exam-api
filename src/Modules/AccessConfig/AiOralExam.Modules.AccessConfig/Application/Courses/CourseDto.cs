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
    [property: Required, MaxLength(20)] string Code,
    [property: Required, MaxLength(200)] string Name);

public sealed record UpdateCourseRequest(
    [property: Required, MaxLength(200)] string Name);

