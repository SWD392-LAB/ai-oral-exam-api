namespace AiOralExam.Modules.AccessConfig.Application.Participants;

public sealed record ParticipantDto(
    Guid Id,
    Guid StudentId,
    string StudentCode,
    string? StudentName,
    string? Email,
    string Status,
    DateTime JoinedAt);

public sealed record ImportStudentsResult(
    int Imported,
    int Skipped,
    IReadOnlyList<string> SkippedStudentCodes,
    IReadOnlyList<ParticipantDto> Participants);
