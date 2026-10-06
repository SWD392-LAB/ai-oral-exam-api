using System.Text;

namespace AiOralExam.Modules.Interview.Application.Ai.Mock;

/// <summary>Mock STT/TTS/LLM cho M1 - M2 thay bang adapter that.</summary>
internal sealed class MockSpeechToTextService : ISpeechToTextService
{
    public Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken ct = default) =>
        Task.FromResult("[Mock STT] Đây là transcript giả lập của câu trả lời.");
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
