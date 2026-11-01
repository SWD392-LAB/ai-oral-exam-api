using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview.Application.Ai;

/// <summary>Local Ollama chat adapter; requests JSON and never sends data to a hosted provider.</summary>
internal sealed class OllamaLlmService(
    HttpClient http,
    IOptions<AiProviderOptions> options) : ILlmService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.OllamaEndpoint) || string.IsNullOrWhiteSpace(settings.OllamaModel))
            throw new AiEvaluationException("Ollama endpoint or model is not configured.");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(settings.OllamaEndpoint, new
            {
                model = settings.OllamaModel,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                format = "json",
                stream = false,
                options = new { temperature = 0.1 }
            }, JsonOptions, ct);
        }
        catch (HttpRequestException)
        {
            throw new AiEvaluationException("Ollama network request failed.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiEvaluationException("Ollama request timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new AiEvaluationException($"Ollama request failed with HTTP {(int)response.StatusCode}.");

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var text = document.RootElement.GetProperty("message").GetProperty("content").GetString();
                return string.IsNullOrWhiteSpace(text)
                    ? throw new AiEvaluationException("Ollama returned an empty response.")
                    : text;
            }
            catch (AiEvaluationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new AiEvaluationException("Ollama returned an invalid response envelope.");
            }
        }
    }
}
