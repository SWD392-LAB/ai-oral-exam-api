namespace AiOralExam.Modules.AccessConfig.Application.Courses;

public interface ICourseService
{
    Task<IReadOnlyList<CourseDto>> GetAllAsync(CancellationToken ct);
    Task<IReadOnlyList<AdminCourseDto>> GetAllWithLecturersAsync(CancellationToken ct);
    Task<CourseDto> GetAsync(Guid id, CancellationToken ct);
    Task<CourseDto> CreateAsync(CreateCourseRequest r, CancellationToken ct);
    Task<CourseDto> UpdateAsync(Guid id, UpdateCourseRequest r, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
