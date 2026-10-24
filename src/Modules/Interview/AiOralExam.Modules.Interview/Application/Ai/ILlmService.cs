namespace AiOralExam.Modules.Interview.Application.Ai;

public interface ILlmService
{
    /// <summary>Calls the LLM and returns text (the real adapter parses JSON if needed).</summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
