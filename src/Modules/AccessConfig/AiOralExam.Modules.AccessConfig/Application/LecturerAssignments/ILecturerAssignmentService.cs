namespace AiOralExam.Modules.AccessConfig.Application.LecturerAssignments;

public interface ILecturerAssignmentService
{
    Task<IReadOnlyList<LecturerDto>> ListAsync(Guid courseId, CancellationToken ct);
    Task ValidateLecturerAsync(Guid lecturerId, CancellationToken ct);
    Task<LecturerDto> AssignAsync(Guid courseId, Guid lecturerId, CancellationToken ct);
    Task RemoveAsync(Guid courseId, Guid lecturerId, CancellationToken ct);
}
