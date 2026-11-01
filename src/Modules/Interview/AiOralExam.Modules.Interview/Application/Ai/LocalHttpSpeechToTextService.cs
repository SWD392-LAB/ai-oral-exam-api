using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview.Application.Ai;

/// <summary>
/// Adapter for a local faster-whisper HTTP sidecar. The sidecar stays outside
/// the API process so the .NET service has no model/runtime dependency.
/// </summary>
internal sealed class LocalHttpSpeechToTextService(
    HttpClient http,
    IOptions<AiProviderOptions> options) : ISpeechToTextService
{
    public async Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.LocalSttEndpoint))
            throw new AiEvaluationException("Local STT endpoint is not configured.");

        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(audio);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(content, "file", "answer-audio");
        form.Add(new StringContent(language), "language");

        using var response = await http.PostAsync(settings.LocalSttEndpoint, form, ct);
        if (!response.IsSuccessStatusCode)
            throw new AiEvaluationException($"Local STT request failed with HTTP {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var json = JsonDocument.Parse(body);
            var text = json.RootElement.TryGetProperty("text", out var value) ? value.GetString() : null;
            return string.IsNullOrWhiteSpace(text)
                ? throw new AiEvaluationException("Local STT returned an empty transcript.")
                : text.Trim();
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(body)
                ? throw new AiEvaluationException("Local STT returned an empty transcript.")
                : body.Trim();
        }
    }
}
