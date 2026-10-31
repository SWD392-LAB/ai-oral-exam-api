using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview.Application.Ai;

/// <summary>Adapter for a local Piper/Coqui-compatible TTS HTTP sidecar.</summary>
internal sealed class LocalHttpTextToSpeechService(
    HttpClient http,
    IOptions<AiProviderOptions> options) : ITextToSpeechService
{
    public async Task<(byte[] Audio, string ContentType)> SynthesizeAsync(string text, string language, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.LocalTtsEndpoint))
            throw new AiEvaluationException("Local TTS endpoint is not configured.");

        using var response = await http.PostAsJsonAsync(settings.LocalTtsEndpoint, new { text, language }, ct);
        if (!response.IsSuccessStatusCode)
            throw new AiEvaluationException($"Local TTS request failed with HTTP {(int)response.StatusCode}.");

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/wav";
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var json = JsonDocument.Parse(bytes);
                var base64 = json.RootElement.GetProperty("audioBase64").GetString();
                bytes = string.IsNullOrWhiteSpace(base64)
                    ? throw new AiEvaluationException("Local TTS returned empty audio.")
                    : Convert.FromBase64String(base64);
                contentType = json.RootElement.TryGetProperty("contentType", out var type)
                    ? type.GetString() ?? "audio/wav"
                    : "audio/wav";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException)
            {
                throw new AiEvaluationException("Local TTS returned invalid audio JSON.");
            }
        }

        return bytes.Length == 0
            ? throw new AiEvaluationException("Local TTS returned empty audio.")
            : (bytes, contentType);
    }
}
