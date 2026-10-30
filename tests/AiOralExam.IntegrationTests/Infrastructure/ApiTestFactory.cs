using System.Security.Claims;
using System.Text.Encodings.Web;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IKeycloakAdminClient = AiOralExam.Modules.AccessConfig.Application.Users.Keycloak.IKeycloakAdminClient;

namespace AiOralExam.IntegrationTests.Infrastructure;

public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    private readonly string? _postgresConnectionString;
    private readonly string _accessConfigDatabase = $"access-config-tests-{Guid.NewGuid():N}";
    private readonly string _interviewDatabase = $"interview-tests-{Guid.NewGuid():N}";
    private Guid _authenticatedUserId;
    private string _authenticatedRole = Roles.Student;
    private bool _authenticated = true;
    private TestAuthenticationIdentity? _testIdentity;
    private TestSpeechToTextService? _testSpeechToText;

    internal Guid AuthenticatedUserId => _authenticatedUserId;
    internal string AuthenticatedRole => _authenticatedRole;
    internal TestAiInvocationCounters AiInvocationCounters { get; } = new();
    internal Exception? LastServerException { get; private set; }

    internal ApiTestFactory(string? postgresConnectionString = null)
    {
        _postgresConnectionString = postgresConnectionString;
    }

    internal ApiTestFactory AuthenticateAs(Guid userId, string role)
    {
        _authenticated = true;
        _authenticatedUserId = userId;
        _authenticatedRole = role;
        _testIdentity?.Set(userId, role);
        return this;
    }

    internal ApiTestFactory AuthenticateAnonymously()
    {
        _authenticated = false;
        _testIdentity?.SetAuthentication(false);
        return this;
    }

    internal void FailSpeechToText() => _testSpeechToText?.FailTranscription();

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

        builder.ConfigureLogging(logging =>
            logging.AddProvider(new TestExceptionLoggerProvider(exception => LastServerException = exception)));

        builder.ConfigureTestServices(services =>
        {
            var analyzerDescriptor = services.Single(service => service.ServiceType == typeof(IAnswerAnalyzer));
            var scorerDescriptor = services.Single(service => service.ServiceType == typeof(IRubricScorer));

            _testIdentity = new TestAuthenticationIdentity(_authenticatedUserId, _authenticatedRole, _authenticated);
            services.AddSingleton(_testIdentity);
            services.AddSingleton(AiInvocationCounters);

            services.RemoveAll<IAnswerAnalyzer>();
            services.AddSingleton<IAnswerAnalyzer>(serviceProvider => new CountingAnswerAnalyzer(
                CreateOriginalService<IAnswerAnalyzer>(serviceProvider, analyzerDescriptor),
                serviceProvider.GetRequiredService<TestAiInvocationCounters>()));

            services.RemoveAll<IRubricScorer>();
            services.AddSingleton<IRubricScorer>(serviceProvider => new CountingRubricScorer(
                CreateOriginalService<IRubricScorer>(serviceProvider, scorerDescriptor),
                serviceProvider.GetRequiredService<TestAiInvocationCounters>()));

            services.RemoveAll<ISpeechToTextService>();
            _testSpeechToText = new TestSpeechToTextService();
            services.AddSingleton(_testSpeechToText);
            services.AddSingleton<ISpeechToTextService>(serviceProvider =>
                serviceProvider.GetRequiredService<TestSpeechToTextService>());

            services.RemoveAll<AccessConfigDbContext>();
            services.RemoveAll<DbContextOptions<AccessConfigDbContext>>();
            services.AddScoped(_ => new AccessConfigDbContext(
                BuildAccessConfigOptions()));

            services.RemoveAll<InterviewDbContext>();
            services.RemoveAll<DbContextOptions<InterviewDbContext>>();
            services.AddScoped(_ => new InterviewDbContext(
                BuildInterviewOptions()));

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
            // Disable automatic deadline worker only for PostgreSQL integration tests.
            // Recovery is invoked manually by the tests to avoid race conditions.
            if (_postgresConnectionString is not null)
            {
                var deadlineWorker = services.FirstOrDefault(service =>
                    service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType?.Name == "InterviewAttemptDeadlineWorker");

                if (deadlineWorker is not null)
                    services.Remove(deadlineWorker);
            }
        });
    }

    private static T CreateOriginalService<T>(IServiceProvider serviceProvider, ServiceDescriptor descriptor)
        where T : class
    {
        if (descriptor.ImplementationInstance is T instance)
            return instance;
        if (descriptor.ImplementationFactory is not null)
            return (T)descriptor.ImplementationFactory(serviceProvider);
        if (descriptor.ImplementationType is not null)
            return (T)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType);
        throw new InvalidOperationException($"No implementation was registered for {typeof(T).Name}.");
    }

    private DbContextOptions<InterviewDbContext> BuildInterviewOptions()
    {
        var builder = new DbContextOptionsBuilder<InterviewDbContext>();
        if (_postgresConnectionString is null)
            builder.UseInMemoryDatabase(_interviewDatabase);
        else
            builder.UseNpgsql(_postgresConnectionString).UseSnakeCaseNamingConvention();
        return builder.Options;
    }

    private DbContextOptions<AccessConfigDbContext> BuildAccessConfigOptions()
    {
        var builder = new DbContextOptionsBuilder<AccessConfigDbContext>();
        if (_postgresConnectionString is null)
        {
            builder.UseInMemoryDatabase(_accessConfigDatabase)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
        else
        {
            builder.UseNpgsql(_postgresConnectionString).UseSnakeCaseNamingConvention();
        }
        return builder.Options;
    }
}

internal sealed class TestAiInvocationCounters
{
    private string? _targetAnswerMarker;
    private int _targetAnalyzerCalls;
    private int _targetScorerCalls;

    internal int TargetAnalyzerCalls => Volatile.Read(ref _targetAnalyzerCalls);
    internal int TargetScorerCalls => Volatile.Read(ref _targetScorerCalls);

    internal void SetTargetAnswerMarker(string marker) => _targetAnswerMarker = marker;

    internal bool IsTarget(IReadOnlyList<DialogueTurn> dialogue) =>
        _targetAnswerMarker is { } marker
        && dialogue.Any(turn => turn.Answer?.Contains(marker, StringComparison.Ordinal) == true);

    internal void RecordAnalyzerCall() => Interlocked.Increment(ref _targetAnalyzerCalls);
    internal void RecordScorerCall() => Interlocked.Increment(ref _targetScorerCalls);
}

internal sealed class CountingAnswerAnalyzer(IAnswerAnalyzer inner, TestAiInvocationCounters counters) : IAnswerAnalyzer
{
    public Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default)
    {
        if (counters.IsTarget(request.Dialogue))
            counters.RecordAnalyzerCall();
        return inner.AnalyzeAsync(request, ct);
    }
}

internal sealed class CountingRubricScorer(IRubricScorer inner, TestAiInvocationCounters counters) : IRubricScorer
{
    public Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default)
    {
        if (counters.IsTarget(request.Dialogue))
            counters.RecordScorerCall();
        return inner.ScoreAsync(request, ct);
    }
}

internal sealed class TestSpeechToTextService : ISpeechToTextService
{
    private int _failTranscription;

    internal void FailTranscription() => Interlocked.Exchange(ref _failTranscription, 1);

    public Task<string> TranscribeAsync(
        Stream audio, string contentType, string language, CancellationToken ct = default) =>
        Volatile.Read(ref _failTranscription) == 1
            ? throw new InvalidOperationException("Deterministic integration-test STT failure.")
            : Task.FromResult("[Integration Test STT] deterministic transcript");
}

internal sealed class TestExceptionLoggerProvider(Action<Exception> capture) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TestExceptionLogger(categoryName, capture);

    public void Dispose()
    {
    }
}

internal sealed class TestExceptionLogger(string categoryName, Action<Exception> capture) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (exception is not null
            && categoryName.EndsWith(".ExceptionHandlingMiddleware", StringComparison.Ordinal))
            capture(exception);
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
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
        if (!testIdentity.IsAuthenticated)
            return Task.FromResult(AuthenticateResult.NoResult());
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
    public Task<Guid> CreateUserAsync(
        string username, string email, string firstName, string lastName, CancellationToken ct = default) =>
        Task.FromResult(Guid.NewGuid());

    public Task AssignRealmRoleAsync(Guid userId, string roleName, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> SendSetPasswordEmailAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task DeleteUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.CompletedTask;
}

internal sealed class TestAuthenticationIdentity(Guid userId, string role, bool isAuthenticated)
{
    public Guid UserId { get; private set; } = userId;
    public string Role { get; private set; } = role;
    public bool IsAuthenticated { get; private set; } = isAuthenticated;

    public void Set(Guid userId, string role)
    {
        UserId = userId;
        Role = role;
        IsAuthenticated = true;
    }

    public void SetAuthentication(bool isAuthenticated) => IsAuthenticated = isAuthenticated;
}
