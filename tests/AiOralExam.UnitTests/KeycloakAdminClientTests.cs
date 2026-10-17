using System.Net;
using System.Text;
using System.Text.Json;
using AiOralExam.Modules.AccessConfig;
using AiOralExam.Modules.AccessConfig.Application.Keycloak;
using AiOralExam.SharedKernel.Errors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOralExam.UnitTests;

public class KeycloakAdminClientTests
{
    [Fact]
    public async Task Create_user_posts_keycloak_user_and_returns_id_from_location()
    {
        var userId = Guid.NewGuid();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/protocol/openid-connect/token", StringComparison.Ordinal)
            ? TokenResponse()
            : CreatedUserResponse(userId));
        var client = CreateClient(handler);

        var result = await client.CreateUserAsync("sample.user", "sample@example.test", "Sample", "User");

        Assert.Equal(userId, result);
        var request = Assert.Single(handler.Requests.Where(x => x.Uri.AbsolutePath.EndsWith("/admin/realms/aives/users", StringComparison.Ordinal)));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer access-token", request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("sample.user", body.RootElement.GetProperty("username").GetString());
        Assert.Equal("sample@example.test", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("Sample", body.RootElement.GetProperty("firstName").GetString());
        Assert.Equal("User", body.RootElement.GetProperty("lastName").GetString());
        Assert.True(body.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(body.RootElement.GetProperty("emailVerified").GetBoolean());
    }

    [Theory]
    [InlineData("User exists with same email", "email_exists")]
    [InlineData("User exists with same username", "username_exists")]
    public async Task Create_user_maps_keycloak_conflicts(string message, string expectedCode)
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/protocol/openid-connect/token", StringComparison.Ordinal)
            ? TokenResponse()
            : new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent($"{{\"errorMessage\":\"{message}\"}}", Encoding.UTF8, "application/json")
            });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            client.CreateUserAsync("sample.user", "sample@example.test", "Sample", "User"));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task Create_user_maps_keycloak_server_errors_to_bad_gateway()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/protocol/openid-connect/token", StringComparison.Ordinal)
            ? TokenResponse()
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            client.CreateUserAsync("sample.user", "sample@example.test", "Sample", "User"));

        Assert.Equal(502, exception.StatusCode);
        Assert.Equal("keycloak_unavailable", exception.Code);
    }

    [Fact]
    public async Task Token_unauthorized_maps_to_admin_client_not_configured()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            client.CreateUserAsync("sample.user", "sample@example.test", "Sample", "User"));

        Assert.Equal("keycloak_admin_client_not_configured", exception.Code);
    }

    [Fact]
    public async Task Admin_api_forbidden_maps_to_admin_forbidden()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/protocol/openid-connect/token", StringComparison.Ordinal)
            ? TokenResponse()
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            client.CreateUserAsync("sample.user", "sample@example.test", "Sample", "User"));

        Assert.Equal("keycloak_admin_forbidden", exception.Code);
    }

    [Fact]
    public void Empty_admin_client_secret_fails_options_validation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KeycloakAdmin:ClientSecret"] = ""
            })
            .Build();
        var services = new ServiceCollection();
        services.AddAccessConfigModule("Host=localhost;Database=test", configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value);

        Assert.Contains(
            "KeycloakAdmin:ClientSecret is not configured. Set it with user-secrets or the KeycloakAdmin__ClientSecret environment variable.",
            exception.Failures);
    }

    [Fact]
    public async Task Client_credentials_token_is_cached_across_user_creations()
    {
        var userId = Guid.NewGuid();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/protocol/openid-connect/token", StringComparison.Ordinal)
            ? TokenResponse()
            : CreatedUserResponse(userId));
        var client = CreateClient(handler);

        await client.CreateUserAsync("first.user", "first@example.test", "First", "User");
        await client.CreateUserAsync("second.user", "second@example.test", "Second", "User");

        Assert.Single(handler.Requests.Where(x => x.Uri.AbsolutePath.EndsWith(
            "/protocol/openid-connect/token", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    public async Task Setup_email_request_reports_keycloak_acceptance(HttpStatusCode statusCode, bool expected)
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/protocol/openid-connect/token", StringComparison.Ordinal)
            ? TokenResponse()
            : new HttpResponseMessage(statusCode));
        var client = CreateClient(handler);

        var result = await client.SendSetPasswordEmailAsync(Guid.NewGuid());

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Setup_email_request_throws_when_canceled()
    {
        var handler = new RecordingHandler(_ => TokenResponse());
        var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendSetPasswordEmailAsync(Guid.NewGuid(), cancellation.Token));
    }

    private static KeycloakAdminClient CreateClient(RecordingHandler handler)
    {
        var options = Options.Create(new KeycloakAdminOptions
        {
            BaseUrl = "http://keycloak.test",
            Realm = "aives",
            ClientId = "aives-admin-api",
            ClientSecret = "test-secret"
        });
        return new KeycloakAdminClient(new RecordingHttpClientFactory(handler), options,
            NullLogger<KeycloakAdminClient>.Instance);
    }

    private static HttpResponseMessage TokenResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"access_token\":\"access-token\",\"expires_in\":300}",
            Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage CreatedUserResponse(Guid id)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created);
        response.Headers.Location = new Uri($"http://keycloak.test/admin/realms/aives/users/{id:D}");
        return response;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                body));
            return createResponse(request);
        }
    }

    private sealed class RecordingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);
}
