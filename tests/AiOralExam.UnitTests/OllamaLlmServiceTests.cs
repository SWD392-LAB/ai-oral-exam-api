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
        var calls = 0;
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(_ =>
            {
                calls++;
                throw new HttpRequestException("internal endpoint");
            })),
            Options.Create(new AiProviderOptions()));

        var error = await Assert.ThrowsAsync<AiEvaluationException>(() => service.CompleteAsync("system", "user"));

        Assert.DoesNotContain("internal endpoint", error.Message);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Retries_transient_status_codes_up_to_three_attempts(HttpStatusCode transientStatus)
    {
        var calls = 0;
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(_ =>
            {
                calls++;
                return Task.FromResult(calls == 3
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}", Encoding.UTF8, "application/json")
                    }
                    : new HttpResponseMessage(transientStatus));
            })),
            Options.Create(new AiProviderOptions { LlmProvider = "Ollama" }));

        var result = await service.CompleteAsync("system", "user");

        Assert.Equal("{\"ok\":true}", result);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Does_not_retry_non_transient_client_errors(HttpStatusCode statusCode)
    {
        var calls = 0;
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(_ =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent("provider detail")
                });
            })),
            Options.Create(new AiProviderOptions { LlmProvider = "Ollama" }));

        var error = await Assert.ThrowsAsync<AiEvaluationException>(() => service.CompleteAsync("system", "user"));

        Assert.Equal("ai_evaluation_failed", error.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retries_transient_failure_with_bounded_attempts()
    {
        var calls = 0;
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(_ =>
            {
                calls++;
                return Task.FromResult(calls == 3
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}", Encoding.UTF8, "application/json")
                    }
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            })),
            Options.Create(new AiProviderOptions { LlmProvider = "Ollama" }));

        var result = await service.CompleteAsync("system", "user");

        Assert.Equal("{\"ok\":true}", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Rejects_malformed_response_json()
    {
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-json", Encoding.UTF8, "application/json")
            }))),
            Options.Create(new AiProviderOptions { LlmProvider = "Ollama" }));

        await Assert.ThrowsAsync<AiEvaluationException>(() => service.CompleteAsync("system", "user"));
    }

    [Fact]
    public async Task Reports_unavailable_model_without_exposing_provider_response()
    {
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("model details")
            }))),
            Options.Create(new AiProviderOptions { LlmProvider = "Ollama" }));

        var error = await Assert.ThrowsAsync<AiEvaluationException>(() => service.CompleteAsync("system", "user"));

        Assert.Equal("ai_evaluation_failed", error.Code);
        Assert.DoesNotContain("model details", error.Message);
    }

    [Fact]
    public async Task Reports_timeout_as_provider_failure()
    {
        var http = new HttpClient(new StubHttpMessageHandler(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            cancellableResponse: async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }))
        {
            Timeout = TimeSpan.FromMilliseconds(10)
        };
        var service = new OllamaLlmService(http, Options.Create(new AiProviderOptions { LlmProvider = "Ollama" }));

        var error = await Assert.ThrowsAsync<AiEvaluationException>(() => service.CompleteAsync("system", "user"));

        Assert.Equal("ai_evaluation_failed", error.Code);
        var detail = Assert.Single(error.Details!["evaluation"]);
        Assert.Contains("timed out", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_timeout_during_response_body_processing_as_provider_failure()
    {
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowTaskCanceledStream())
            }))),
            Options.Create(new AiProviderOptions { LlmProvider = "Ollama" }));

        var error = await Assert.ThrowsAsync<AiEvaluationException>(() => service.CompleteAsync("system", "user"));

        Assert.Equal("ai_evaluation_failed", error.Code);
        var detail = Assert.Single(error.Details!["evaluation"]);
        Assert.Contains("timed out", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_operation_canceled_during_response_body_processing_as_provider_failure()
    {
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowOperationCanceledStream())
            }))),
            Options.Create(new AiProviderOptions { LlmProvider = "Ollama" }));

        var error = await Assert.ThrowsAsync<AiEvaluationException>(() => service.CompleteAsync("system", "user"));

        Assert.Equal("ai_evaluation_failed", error.Code);
        var detail = Assert.Single(error.Details!["evaluation"]);
        Assert.Contains("timed out", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Propagates_caller_cancellation_during_response_body_processing()
    {
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new WaitForCancellationStream())
            }))),
            Options.Create(new AiProviderOptions { LlmProvider = "Ollama" }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CompleteAsync("system", "user", cts.Token));
    }

    [Fact]
    public async Task Propagates_caller_cancellation_during_request_send_without_retry()
    {
        var calls = 0;
        var service = new OllamaLlmService(
            new HttpClient(new StubHttpMessageHandler(
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
                cancellableResponse: async (_, ct) =>
                {
                    calls++;
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                })),
            Options.Create(new AiProviderOptions { LlmProvider = "Ollama" }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CompleteAsync("system", "user", cts.Token));
        Assert.Equal(1, calls);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? cancellableResponse = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            cancellableResponse is null
                ? response(request)
                : cancellableResponse(request, cancellationToken);
    }

    private sealed class ThrowTaskCanceledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new TaskCanceledException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new TaskCanceledException());
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new TaskCanceledException());

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class WaitForCancellationStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowOperationCanceledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new OperationCanceledException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new OperationCanceledException());
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new OperationCanceledException());

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
