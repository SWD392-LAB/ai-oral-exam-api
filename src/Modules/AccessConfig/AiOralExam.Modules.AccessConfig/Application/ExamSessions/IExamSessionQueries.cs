namespace AiOralExam.Modules.AccessConfig.Application.ExamSessions;

/// <summary>
/// Read exam sessions for lecturers / admins.
/// TODO (M4): CreateSession, ImportQuestions, DefineRubric, Publish (locks questions + rubrics), write audit log.
/// </summary>
public interface IExamSessionQueries
{
    Task<IReadOnlyList<CourseDto>> GetMyCoursesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExamSessionSummaryDto>> GetSessionsAsync(Guid? courseId, CancellationToken ct = default);
    Task<ExamSessionDetailDto> GetSessionAsync(Guid id, CancellationToken ct = default);
}
