using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview.Application.Ai;

/// <summary>Local Ollama chat adapter; requests JSON and never sends data to a hosted provider.</summary>
internal sealed class OllamaLlmService(
    HttpClient http,
    IOptions<AiProviderOptions> options) : ILlmService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxAttempts = 3;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.OllamaEndpoint) || string.IsNullOrWhiteSpace(settings.OllamaModel))
            throw new AiEvaluationException("Ollama endpoint or model is not configured.");

        using var response = await SendWithRetryAsync(settings, systemPrompt, userPrompt, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            throw new AiEvaluationException("Ollama model or endpoint is unavailable.");

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
        // Non-caller cancellation at this stage is treated as provider timeout.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiEvaluationException("Ollama request timed out.");
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

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        AiProviderOptions settings,
        string systemPrompt,
        string userPrompt,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.OllamaEndpoint)
                {
                    Content = JsonContent.Create(new
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
                    }, options: JsonOptions)
                };
                var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!IsTransient(response.StatusCode) || attempt == MaxAttempts)
                    return response;

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
            }
            catch (HttpRequestException)
            {
                throw new AiEvaluationException("Ollama network request failed.");
            }
            // HttpClient timeout (or equivalent provider-side cancel) should be mapped to provider failure.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AiEvaluationException("Ollama request timed out.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
        }

        throw new AiEvaluationException("Ollama network request failed.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || statusCode == (HttpStatusCode)429
        || (int)statusCode >= 500;
}
