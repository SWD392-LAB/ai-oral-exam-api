using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview.Application.Ai;

/// <summary>Small HTTP adapter for Gemini's JSON generation endpoint.</summary>
internal sealed class GeminiLlmService(
    HttpClient http,
    IOptions<AiProviderOptions> options) : ILlmService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.GeminiApiKey))
            throw new AiEvaluationException("Gemini API key is not configured.");

        var endpoint = $"{settings.GeminiEndpoint.TrimEnd('/')}/models/{Uri.EscapeDataString(settings.GeminiModel)}:generateContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
                generationConfig = new { responseMimeType = "application/json", temperature = 0.1 }
            }, options: JsonOptions)
        };
        request.Headers.Add("x-goog-api-key", settings.GeminiApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new AiEvaluationException("Gemini network request failed.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiEvaluationException("Gemini request timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new AiEvaluationException($"Gemini request failed with HTTP {(int)response.StatusCode}.");

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var candidates = document.RootElement.GetProperty("candidates");
                var text = candidates.GetArrayLength() == 0
                    ? null
                    : candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

                return string.IsNullOrWhiteSpace(text)
                    ? throw new AiEvaluationException("Gemini returned an empty response.")
                    : text;
            }
            catch (AiEvaluationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                throw new AiEvaluationException("Gemini returned an invalid response envelope.");
            }
        }
    }
}
