namespace AiOralExam.Modules.Interview.Application.Ai;

/// <summary>
/// Provider switches for the interview AI pipeline. Mock is deliberately the
/// default so local development and tests never require an account or billing.
/// </summary>
public sealed class AiProviderOptions
{
    public const string SectionName = "AiProviders";

    public string LlmProvider { get; set; } = "Mock";
    public string SttProvider { get; set; } = "Mock";
    public string TtsProvider { get; set; } = "Mock";

    public string GeminiApiKey { get; set; } = "";
    public string GeminiEndpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public string GeminiModel { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>HTTP endpoint for a local faster-whisper service.</summary>
    public string LocalSttEndpoint { get; set; } = "http://localhost:8001/transcribe";

    /// <summary>HTTP endpoint for a local Piper/Coqui-compatible TTS service.</summary>
    public string LocalTtsEndpoint { get; set; } = "http://localhost:8002/synthesize";
    public string SpeechLanguage { get; set; } = "vi-VN";

    /// <summary>Private local object-storage directory used by the demo audio adapter.</summary>
    public string AudioStorageRoot { get; set; } = "audio-storage";

    public long MaxAudioBytes { get; set; } = 15 * 1024 * 1024;
    public int RequestTimeoutSeconds { get; set; } = 60;

    public bool UsesGemini => string.Equals(LlmProvider, "Gemini", StringComparison.OrdinalIgnoreCase);
    public bool UsesLocalStt => string.Equals(SttProvider, "LocalHttp", StringComparison.OrdinalIgnoreCase);
    public bool UsesLocalTts => string.Equals(TtsProvider, "LocalHttp", StringComparison.OrdinalIgnoreCase);
}
