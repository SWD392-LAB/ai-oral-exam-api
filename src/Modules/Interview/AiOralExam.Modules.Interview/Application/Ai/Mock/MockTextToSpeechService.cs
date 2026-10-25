using System.Text;

namespace AiOralExam.Modules.Interview.Application.Ai.Mock;

internal sealed class MockTextToSpeechService : ITextToSpeechService
{
    public Task<(byte[] Audio, string ContentType)> SynthesizeAsync(string text, string language, CancellationToken ct = default) =>
        Task.FromResult((Encoding.UTF8.GetBytes(text), "text/plain"));
}
