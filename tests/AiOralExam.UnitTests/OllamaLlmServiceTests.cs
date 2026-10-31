using System.Net;
using System.Text;
using AiOralExam.Modules.Interview.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiOralExam.UnitTests;

public sealed class OllamaLlmServiceTests
{
    [Fact]
    public async Task Sends_json_chat_and_returns_model_text()
    {
        var handler = new StubHttpMessageHandler(async request =>
        {
            Assert.Equal("http://localhost:11434/api/chat", request.RequestUri!.ToString());
            Assert.Empty(request.Headers);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"model\":\"qwen2.5:3b\"", body);
            Assert.Contains("\"format\":\"json\"", body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}", Encoding.UTF8, "application/json")
            };
        });
        var service = new OllamaLlmService(new HttpClient(handler), Options.Create(new AiProviderOptions
        {
            LlmProvider = "Ollama"
        }));

        var result = await service.CompleteAsync("system", "user");

        Assert.Equal("{\"ok\":true}", result);
    }

    [Fact]
    public async Task Provider_failure_is_sanitized()
    {
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new HttpRequestException("internal endpoint"))),
            Options.Create(new AiProviderOptions()));

        var error = await Assert.ThrowsAsync<AiEvaluationException>(() => service.CompleteAsync("system", "user"));

        Assert.DoesNotContain("internal endpoint", error.Message);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request);
    }
}
