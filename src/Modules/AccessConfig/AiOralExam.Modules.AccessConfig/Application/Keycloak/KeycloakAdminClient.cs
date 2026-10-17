using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiOralExam.SharedKernel.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.AccessConfig.Application.Keycloak;

public sealed class KeycloakAdminOptions
{
    public const string SectionName = "KeycloakAdmin";

    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string Realm { get; set; } = "aives";
    public string ClientId { get; set; } = "aives-admin-api";
    public string ClientSecret { get; set; } = null!;
}

public interface IKeycloakAdminClient
{
    Task<Guid> CreateUserAsync(string username, string email, string firstName, string lastName, CancellationToken ct = default);
    Task AssignRealmRoleAsync(Guid userId, string roleName, CancellationToken ct = default);
    Task<bool> SendSetPasswordEmailAsync(Guid userId, CancellationToken ct = default);
    Task DeleteUserAsync(Guid userId, CancellationToken ct = default);
}

internal sealed class KeycloakAdminClient(
    IHttpClientFactory httpClientFactory,
    IOptions<KeycloakAdminOptions> options,
    ILogger<KeycloakAdminClient> logger) : IKeycloakAdminClient
{
    internal const string HttpClientName = "KeycloakAdmin";
    private const int TokenExpiryLeewaySeconds = 30;

    private readonly string _baseUrl = options.Value.BaseUrl.TrimEnd('/');
    private readonly string _realm = Uri.EscapeDataString(options.Value.Realm);
    private readonly string _clientId = options.Value.ClientId;
    private readonly string _clientSecret = options.Value.ClientSecret;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private CachedToken? _cachedToken;

    public async Task<Guid> CreateUserAsync(
        string username, string email, string firstName, string lastName, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var token = await GetAccessTokenAsync(client, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, AdminUrl("users"))
        {
            Content = JsonContent.Create(new KeycloakUser(username, email, firstName, lastName))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(client, request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw await MapCreateConflictAsync(response, ct);
        EnsureSuccess(response);

        var location = response.Headers.Location?.OriginalString;
        var idText = location?.TrimEnd('/').Split('/').LastOrDefault();
        if (!Guid.TryParse(idText, out var id))
            throw KeycloakUnavailable();
        return id;
    }

    public async Task AssignRealmRoleAsync(Guid userId, string roleName, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var token = await GetAccessTokenAsync(client, ct);
        using var roleRequest = new HttpRequestMessage(HttpMethod.Get,
            AdminUrl($"roles/{Uri.EscapeDataString(roleName)}"));
        roleRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var roleResponse = await SendAsync(client, roleRequest, ct);
        EnsureSuccess(roleResponse);

        JsonElement role;
        try
        {
            role = await roleResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw KeycloakUnavailable();
        }

        using var mappingRequest = new HttpRequestMessage(HttpMethod.Post,
            AdminUrl($"users/{userId:D}/role-mappings/realm"))
        {
            Content = JsonContent.Create(new[] { role })
        };
        mappingRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var mappingResponse = await SendAsync(client, mappingRequest, ct);
        EnsureSuccess(mappingResponse);
    }

    public async Task<bool> SendSetPasswordEmailAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            var token = await GetAccessTokenAsync(client, ct);
            using var request = new HttpRequestMessage(HttpMethod.Put,
                AdminUrl($"users/{userId:D}/execute-actions-email"))
            {
                Content = JsonContent.Create(new[] { "UPDATE_PASSWORD" })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await SendAsync(client, request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Keycloak password setup email request failed for user {UserId} with status {StatusCode}.",
                    userId, response.StatusCode);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not request a password setup email for Keycloak user {UserId}.", userId);
            return false;
        }
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var token = await GetAccessTokenAsync(client, ct);
        using var request = new HttpRequestMessage(HttpMethod.Delete, AdminUrl($"users/{userId:D}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(client, request, ct);
        EnsureSuccess(response);
    }

    private async Task<string> GetAccessTokenAsync(HttpClient client, CancellationToken ct)
    {
        var cached = Volatile.Read(ref _cachedToken);
        if (cached is not null && DateTimeOffset.UtcNow < cached.ExpiresAt)
            return cached.Value;

        await _tokenLock.WaitAsync(ct);
        try
        {
            cached = Volatile.Read(ref _cachedToken);
            if (cached is not null && DateTimeOffset.UtcNow < cached.ExpiresAt)
                return cached.Value;

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_baseUrl}/realms/{_realm}/protocol/openid-connect/token")
            {
                Content = new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", _clientId),
                    new KeyValuePair<string, string>("client_secret", _clientSecret)
                ])
            };
            using var response = await SendAsync(client, request, ct);
            EnsureTokenSuccess(response);

            TokenResponse? tokenResponse;
            try
            {
                tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
            }
            catch (JsonException)
            {
                throw KeycloakUnavailable();
            }

            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
                throw KeycloakUnavailable();

            var expiresIn = Math.Max(0, tokenResponse.ExpiresIn - TokenExpiryLeewaySeconds);
            var newToken = new CachedToken(tokenResponse.AccessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
            Volatile.Write(ref _cachedToken, newToken);
            return newToken.Value;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw KeycloakUnavailable();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw KeycloakUnavailable();
        }
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        LogFailedResponse(response);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw KeycloakAdminForbidden();
        throw KeycloakUnavailable();
    }

    private void EnsureTokenSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            var requestPath = response.RequestMessage?.RequestUri?.AbsolutePath ?? "(unknown)";
            logger.LogError("Keycloak rejected the admin client credentials at {RequestPath} with HTTP status {StatusCode}.",
                requestPath, (int)response.StatusCode);
            throw KeycloakAdminClientNotConfigured();
        }

        if (!response.IsSuccessStatusCode)
        {
            LogFailedResponse(response);
            throw KeycloakUnavailable();
        }
    }

    private void LogFailedResponse(HttpResponseMessage response)
    {
        var requestPath = response.RequestMessage?.RequestUri?.AbsolutePath ?? "(unknown)";
        logger.LogWarning("Keycloak admin request to {RequestPath} failed with HTTP status {StatusCode}.",
            requestPath, (int)response.StatusCode);
    }

    private static async Task<ConflictException> MapCreateConflictAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var message = await response.Content.ReadAsStringAsync(ct);
        var isEmail = message.Contains("email", StringComparison.OrdinalIgnoreCase);
        return isEmail
            ? new ConflictException("email_exists", "Email is already in use.")
            : new ConflictException("username_exists", "Username is already in use.");
    }

    private string AdminUrl(string path) => $"{_baseUrl}/admin/realms/{_realm}/{path}";

    private static AppException KeycloakUnavailable()
        => new(502, "keycloak_unavailable", "Keycloak admin service is unavailable.");

    private static AppException KeycloakAdminClientNotConfigured()
        => new(502, "keycloak_admin_client_not_configured",
            "Keycloak rejected the admin client credentials. Check that the aives-admin-api client exists in the realm and that KeycloakAdmin:ClientId and KeycloakAdmin:ClientSecret are correct.");

    private static AppException KeycloakAdminForbidden()
        => new(502, "keycloak_admin_forbidden",
            "Keycloak denied the admin request. The service account may be missing realm-management roles.");

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record KeycloakUser(
        string Username,
        string Email,
        string FirstName,
        string LastName,
        bool Enabled = true,
        bool EmailVerified = false);
}
