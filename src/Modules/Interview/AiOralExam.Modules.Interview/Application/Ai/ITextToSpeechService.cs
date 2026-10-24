namespace AiOralExam.Modules.Interview.Application.Ai;

public interface ITextToSpeechService
{
    /// <summary>Text -> audio (vd. audio/mpeg).</summary>
    Task<(byte[] Audio, string ContentType)> SynthesizeAsync(string text, string language, CancellationToken ct = default);
}
