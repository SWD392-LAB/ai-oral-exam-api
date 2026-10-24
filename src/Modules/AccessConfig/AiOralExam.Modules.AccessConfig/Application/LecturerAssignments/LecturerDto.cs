namespace AiOralExam.Modules.AccessConfig.Application.LecturerAssignments;

public sealed record LecturerDto(
    Guid Id,
    string? FullName,
    string? Email,
    string? LecturerCode,
    DateTime AssignedAt);
