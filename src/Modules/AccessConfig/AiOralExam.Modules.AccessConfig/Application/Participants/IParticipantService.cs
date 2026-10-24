namespace AiOralExam.Modules.AccessConfig.Application.Participants;

public interface IParticipantService
{
    Task<IReadOnlyList<ParticipantDto>> ListAsync(Guid sessionId, CancellationToken ct);
    Task<ImportStudentsResult> ImportAsync(Guid sessionId, ImportStudentsRequest r, CancellationToken ct);
    Task RemoveAsync(Guid sessionId, Guid participantId, CancellationToken ct);
}
