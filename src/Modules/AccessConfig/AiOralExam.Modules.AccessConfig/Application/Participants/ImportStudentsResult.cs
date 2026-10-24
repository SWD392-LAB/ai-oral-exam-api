namespace AiOralExam.Modules.AccessConfig.Application.Participants;

public sealed record ImportStudentsResult(
    int Imported,
    int Skipped,
    IReadOnlyList<string> SkippedStudentCodes,
    IReadOnlyList<ParticipantDto> Participants);
