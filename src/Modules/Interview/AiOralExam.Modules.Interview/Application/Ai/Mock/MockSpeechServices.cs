using System.Text;

namespace AiOralExam.Modules.Interview.Application.Ai.Mock;

/// <summary>Mock STT/TTS/LLM for M1 - replaced by real adapters in M2.</summary>
internal sealed class MockSpeechToTextService : ISpeechToTextService
{
    public Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken ct = default) =>
        Task.FromResult("[Mock STT] This is a simulated transcript of the answer.");
}

internal sealed class MockTextToSpeechService : ITextToSpeechService
{
    public Task<(byte[] Audio, string ContentType)> SynthesizeAsync(string text, string language, CancellationToken ct = default) =>
        Task.FromResult((Encoding.UTF8.GetBytes(text), "text/plain"));
}

internal sealed class MockLlmService : ILlmService
{
    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        Task.FromResult("{\"mock\": true}");
}
