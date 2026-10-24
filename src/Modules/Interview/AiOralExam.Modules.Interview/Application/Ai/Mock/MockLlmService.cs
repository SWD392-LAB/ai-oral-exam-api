namespace AiOralExam.Modules.Interview.Application.Ai.Mock;

internal sealed class MockLlmService : ILlmService
{
    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        Task.FromResult("{\"mock\": true}");
}
