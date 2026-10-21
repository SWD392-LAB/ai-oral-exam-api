using System.Security.Claims;
using System.Text.Encodings.Web;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IKeycloakAdminClient = AiOralExam.Modules.AccessConfig.Application.Keycloak.IKeycloakAdminClient;

namespace AiOralExam.IntegrationTests.Infrastructure;

public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    private readonly string _accessConfigDatabase = $"access-config-tests-{Guid.NewGuid():N}";
    private readonly string _interviewDatabase = $"interview-tests-{Guid.NewGuid():N}";
    private Guid _authenticatedUserId;
    private string _authenticatedRole = Roles.Student;

    internal Guid AuthenticatedUserId => _authenticatedUserId;
    internal string AuthenticatedRole => _authenticatedRole;

    internal ApiTestFactory AuthenticateAs(Guid userId, string role)
    {
        _authenticatedUserId = userId;
        _authenticatedRole = role;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=1;Database=integration_tests;Username=test;Password=test",
                ["Keycloak:Authority"] = "https://keycloak.invalid/realms/test",
                ["Keycloak:Audience"] = "integration-tests",
                ["Keycloak:SwaggerClientId"] = "integration-tests",
                ["KeycloakAdmin:ClientSecret"] = "integration-test-secret",
            }));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new TestAuthenticationIdentity(_authenticatedUserId, _authenticatedRole));

            services.RemoveAll<AccessConfigDbContext>();
            services.RemoveAll<DbContextOptions<AccessConfigDbContext>>();
            services.AddScoped(_ => new AccessConfigDbContext(
                new DbContextOptionsBuilder<AccessConfigDbContext>()
                    .UseInMemoryDatabase(_accessConfigDatabase)
                    .Options));

            services.RemoveAll<InterviewDbContext>();
            services.RemoveAll<DbContextOptions<InterviewDbContext>>();
            services.AddScoped(_ => new InterviewDbContext(
                new DbContextOptionsBuilder<InterviewDbContext>()
                    .UseInMemoryDatabase(_interviewDatabase)
                    .Options));

            services.RemoveAll<IKeycloakAdminClient>();
            services.AddSingleton<IKeycloakAdminClient, FakeKeycloakAdminClient>();

            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });

            // This worker uses ExecuteUpdateAsync, which the InMemory provider does not support.
            var autoCloseService = services.FirstOrDefault(service =>
                service.ServiceType == typeof(IHostedService)
                && service.ImplementationType?.Name == "ExamSessionAutoCloseService");
            if (autoCloseService is not null)
                services.Remove(autoCloseService);
        });
    }
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var testIdentity = Context.RequestServices.GetRequiredService<TestAuthenticationIdentity>();
        var claims = new[]
        {
            new Claim(AppClaims.UserId, testIdentity.UserId.ToString()),
            new Claim(AppClaims.Role, testIdentity.Role),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name, AppClaims.Name, AppClaims.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class FakeKeycloakAdminClient : IKeycloakAdminClient
{
    private static readonly Guid CreatedUserId = Guid.Parse("7b4b6116-0d14-4be7-8e15-76f0886de301");

    public Task<Guid> CreateUserAsync(
        string username, string email, string firstName, string lastName, CancellationToken ct = default) =>
        Task.FromResult(CreatedUserId);

    public Task AssignRealmRoleAsync(Guid userId, string roleName, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> SendSetPasswordEmailAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task DeleteUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.CompletedTask;
}

internal sealed record TestAuthenticationIdentity(Guid UserId, string Role);
