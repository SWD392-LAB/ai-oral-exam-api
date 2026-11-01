using System.Text.RegularExpressions;

namespace AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;

internal sealed record QuestionSourceEvidence(string SourceId, int SourceIndex, string Citation, string Text);

internal sealed record QuestionDraftSuggestion(string Content, string SourceId, string Quote);

internal interface IQuestionDraftContentGenerator
{
    string ProviderName { get; }
    Task<IReadOnlyList<QuestionDraftSuggestion>> GenerateAsync(
        QuestionGenerationRequest request,
        IReadOnlyList<QuestionSourceEvidence> evidence,
        CancellationToken ct);
}

internal sealed class DeterministicQuestionDraftContentGenerator : IQuestionDraftContentGenerator
{
    public string ProviderName => "DeterministicDemo";

    public Task<IReadOnlyList<QuestionDraftSuggestion>> GenerateAsync(
        QuestionGenerationRequest request,
        IReadOnlyList<QuestionSourceEvidence> evidence,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var suggestions = Enumerable.Range(0, request.Count)
            .Select(index =>
            {
                var source = evidence[index % evidence.Count];
                var claim = ExtractClaim(source.Text, request.Topic);
                return new QuestionDraftSuggestion(
                    BuildQuestion(request.Topic.Trim(), request.Bloom, index + 1, claim), source.SourceId, claim);
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<QuestionDraftSuggestion>>(suggestions);
    }

    private static string BuildQuestion(string topic, BloomLevel bloom, int number, string claim) => bloom switch
    {
        BloomLevel.Remember => $"[{number}] According to the cited source, what does it state about {topic}? Recall the key detail: “{claim}”",
        BloomLevel.Understand => $"[{number}] Explain the cited source's claim in your own words: “{claim}”",
        BloomLevel.Apply => $"[{number}] Apply the principle in this cited source claim — “{claim}” — to a concrete scenario involving {topic}.",
        BloomLevel.Analyze => $"[{number}] Analyze the cited source claim — “{claim}” — by comparing it with an alternative and justifying when the source's approach is appropriate.",
        _ => throw new ArgumentOutOfRangeException(nameof(bloom))
    };

    private static string ExtractClaim(string source, string topic)
    {
        var sentences = Regex.Matches(source, @"[^.!?\r\n]+[.!?]?")
            .Select(match => string.Join(' ', match.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim())
            .Where(value => value.Length > 0)
            .ToList();
        var topicTerms = topic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim('.', ',', ':', ';', '?', '!'))
            .Where(x => x.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claim = sentences
            .OrderByDescending(sentence => topicTerms.Count(term => sentence.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault() ?? source.Trim();
        if (claim.Length <= 240) return claim;
        var boundary = claim.LastIndexOf(' ', 240);
        return claim[..(boundary > 0 ? boundary : 240)].TrimEnd(',', ';', ':') + "…";
    }
}

public sealed class QuestionGenerationProviderOptions
{
    public const string SectionName = "AiProviders";

    public string LlmProvider { get; set; } = "Mock";
    public string GeminiApiKey { get; set; } = string.Empty;
    public string GeminiEndpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public string GeminiModel { get; set; } = "gemini-3.5-flash-lite";
    public string OllamaEndpoint { get; set; } = "http://localhost:11434/api/chat";
    public string OllamaModel { get; set; } = "qwen2.5:3b";
    public int RequestTimeoutSeconds { get; set; } = 60;

    public bool UsesGemini => string.Equals(LlmProvider, "Gemini", StringComparison.OrdinalIgnoreCase);
    public bool UsesOllama => string.Equals(LlmProvider, "Ollama", StringComparison.OrdinalIgnoreCase);
}
