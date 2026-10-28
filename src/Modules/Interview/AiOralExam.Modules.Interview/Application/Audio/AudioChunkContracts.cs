namespace AiOralExam.Modules.Interview.Application.Audio;

public enum AudioChunkAppendStatus
{
    Appended,
    Duplicate,
    OutOfOrder,
    ConflictingDuplicate,
    UploadCompleted,
    ContentTypeConflict
}

public sealed record AudioChunkCheckpoint(
    int NextSequence,
    long BytesReceived,
    string ContentType,
    bool IsCompleted,
    string? Transcript);

public sealed record AudioChunkAppendResult(
    AudioChunkAppendStatus Status,
    AudioChunkCheckpoint Checkpoint);

public sealed record CompletedAudio(Stream Audio, string ContentType, string? Transcript);

public sealed record AudioChunkCheckpointDto(
    int NextSequence,
    long BytesReceived,
    bool IsCompleted,
    bool Duplicate);

public interface IAudioChunkStore
{
    /// <summary>
    /// Atomically persists each accepted chunk and its checkpoint. The next sequence is accepted;
    /// an identical retry is a duplicate, while gaps or different bytes at an existing sequence are rejected.
    /// Checkpoints and chunks must survive reconnects and process restarts.
    /// </summary>
    Task<AudioChunkAppendResult> AppendAsync(
        Guid attemptId,
        Guid turnId,
        int sequence,
        byte[] chunk,
        string contentType,
        CancellationToken ct);

    Task<AudioChunkCheckpoint?> GetCheckpointAsync(Guid attemptId, Guid turnId, CancellationToken ct);

    /// <summary>Seals the upload and returns the same complete audio on repeated calls.</summary>
    Task<CompletedAudio> CompleteUploadAsync(Guid attemptId, Guid turnId, CancellationToken ct);

    /// <summary>Atomically stores the first transcript and returns the stored transcript on retries.</summary>
    Task<string> SaveTranscriptIfAbsentAsync(Guid attemptId, Guid turnId, string transcript, CancellationToken ct);
}

public interface IInterviewAudioSubmissionService
{
    Task<AudioChunkCheckpointDto> SendAudioChunkAsync(
        Guid attemptId,
        Guid turnId,
        int sequence,
        byte[] chunk,
        string contentType,
        CancellationToken ct = default);

    Task<AudioChunkCheckpointDto?> GetAudioCheckpointAsync(
        Guid attemptId,
        Guid turnId,
        CancellationToken ct = default);

    Task<SubmitAnswerResponse> CompleteAudioAnswerAsync(
        Guid attemptId,
        Guid turnId,
        CancellationToken ct = default);
}

public static class AudioChunkLimits
{
    public const int MaximumChunkBytes = 256 * 1024;
    public const int MaximumSignalRMessageBytes = 384 * 1024;
}
