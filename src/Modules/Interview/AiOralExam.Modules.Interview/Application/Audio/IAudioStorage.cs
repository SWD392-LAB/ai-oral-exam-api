namespace AiOralExam.Modules.Interview.Application.Audio;

public interface IAudioStorage
{
    Task<string> PutAsync(Guid attemptId, Guid turnId, Stream content, string contentType, CancellationToken ct = default);
    Task<StoredAudio?> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed record StoredAudio(Stream Content, string ContentType);
