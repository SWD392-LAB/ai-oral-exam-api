using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.Modules.Interview.Application.Audio;

internal sealed class UnavailableAudioChunkStore : IAudioChunkStore
{
    private static AppException Unavailable() =>
        new(503, "audio_storage_unavailable", "Audio storage is not configured for this deployment.");

    public Task<AudioChunkAppendResult> AppendAsync(
        Guid attemptId, Guid turnId, int sequence, byte[] chunk, string contentType, CancellationToken ct) =>
        Task.FromException<AudioChunkAppendResult>(Unavailable());

    public Task<AudioChunkCheckpoint?> GetCheckpointAsync(Guid attemptId, Guid turnId, CancellationToken ct) =>
        Task.FromException<AudioChunkCheckpoint?>(Unavailable());

    public Task<CompletedAudio> CompleteUploadAsync(Guid attemptId, Guid turnId, CancellationToken ct) =>
        Task.FromException<CompletedAudio>(Unavailable());

    public Task<string> SaveTranscriptIfAbsentAsync(
        Guid attemptId, Guid turnId, string transcript, CancellationToken ct) =>
        Task.FromException<string>(Unavailable());
}

internal sealed class UnavailableSpeechToTextService : ISpeechToTextService
{
    public Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken ct = default) =>
        Task.FromException<string>(new AppException(
            503, "speech_to_text_unavailable", "Speech-to-text is not configured for this deployment."));
}
