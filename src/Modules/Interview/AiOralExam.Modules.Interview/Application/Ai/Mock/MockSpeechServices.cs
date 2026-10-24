using System.Text;

namespace AiOralExam.Modules.Interview.Application.Ai.Mock;

/// <summary>Mock STT/TTS/LLM for M1 - replaced by real adapters in M2.</summary>
internal sealed class MockSpeechToTextService : ISpeechToTextService
{
    public Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken ct = default) =>
        Task.FromResult("[Mock STT] This is a simulated transcript of the answer.");
}
