using System.Net;
using System.Text;
using AiOralExam.Modules.Interview.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiOralExam.UnitTests;

public sealed class GeminiLlmServiceTests
{
    [Fact]
    public async Task Sends_api_key_in_header_instead_of_url_and_returns_model_text()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Empty(request.RequestUri!.Query);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"ok\\\":true}\"}]}}]}",
                    Encoding.UTF8, "application/json")
            };
        });
        var service = new GeminiLlmService(new HttpClient(handler), Options.Create(new AiProviderOptions
        {
            GeminiApiKey = "test-key"
        }));

        var result = await service.CompleteAsync("system", "user");

        Assert.Equal("{\"ok\":true}", result);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
