using AiOralExam.Modules.AccessConfig.Application.LecturerAssignments;
using AiOralExam.Modules.AccessConfig.Application.LecturerAssignments;

namespace AiOralExam.Modules.AccessConfig.Application.Courses;

public sealed record AdminCourseDto(
    Guid Id,
    string Code,
    string Name,
    int LecturerCount,
    int ExamSessionCount,
    IReadOnlyList<LecturerDto> Lecturers);
