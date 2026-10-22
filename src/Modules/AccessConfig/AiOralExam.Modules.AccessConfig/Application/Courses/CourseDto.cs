using System;
using System.Collections.Generic;
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
