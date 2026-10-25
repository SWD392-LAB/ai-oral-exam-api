namespace AiOralExam.Modules.Interview.Application.Ai;

public interface ISpeechToTextService
{
    /// <summary>Audio -> transcript.</summary>
    Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken ct = default);
}
