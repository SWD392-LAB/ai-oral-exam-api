using AiOralExam.Modules.AccessConfig.Application;
using AiOralExam.Modules.AccessConfig.Application.AIServices;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Auth;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.Modules.AccessConfig.Application.LecturerAssignments;
using AiOralExam.Modules.AccessConfig.Application.Participants;
using AiOralExam.Modules.AccessConfig.Application.Questions;
using AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;
using AiOralExam.Modules.AccessConfig.Application.QuestionBank;
using AiOralExam.Modules.AccessConfig.Application.Materials;
using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.AccessConfig.Application.Users;
using AiOralExam.Modules.AccessConfig.Application.Users.CreateUser;
using AiOralExam.Modules.AccessConfig.Application.Users.GetUser;
using AiOralExam.Modules.AccessConfig.Application.Users.Keycloak;
using AiOralExam.Modules.AccessConfig.Application.Users.ListUsers;
using AiOralExam.Modules.AccessConfig.Application.Users.UpdateUser;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.AccessConfig;

public static class AccessConfigModule
{
    public static IServiceCollection AddAccessConfigModule(
        this IServiceCollection services, string connectionString, IConfiguration configuration)
    {
        services.AddDbContext<AccessConfigDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IExamSessionQueries, ExamSessionQueries>();
        services.AddScoped<IExamSessionCommands, ExamSessionCommands>();
        services.AddHostedService<ExamSessionAutoCloseService>();
        services.AddScoped<ICourseService, CourseService>();
        services.AddScoped<IQuestionService, QuestionService>();
        services.Configure<QuestionGenerationProviderOptions>(configuration.GetSection(QuestionGenerationProviderOptions.SectionName));
        services.AddHttpClient<GeminiQuestionDraftContentGenerator>((sp, client) =>
        {
            var seconds = sp.GetRequiredService<IOptions<QuestionGenerationProviderOptions>>().Value.RequestTimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));
        }).RedactLoggedHeaders(header => string.Equals(header, "x-goog-api-key", StringComparison.OrdinalIgnoreCase));
        services.AddHttpClient<OllamaQuestionDraftContentGenerator>((sp, client) =>
        {
            var seconds = sp.GetRequiredService<IOptions<QuestionGenerationProviderOptions>>().Value.RequestTimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));
        });
        services.AddSingleton<DeterministicQuestionDraftContentGenerator>();
        services.AddScoped<IQuestionDraftContentGenerator>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<QuestionGenerationProviderOptions>>().Value;
            if (settings.UsesGemini) return sp.GetRequiredService<GeminiQuestionDraftContentGenerator>();
            if (settings.UsesOllama) return sp.GetRequiredService<OllamaQuestionDraftContentGenerator>();
            return sp.GetRequiredService<DeterministicQuestionDraftContentGenerator>();
        });
        services.AddScoped<IQuestionGenerator, QuestionGenerator>();
        services.AddScoped<IQuestionBankService, QuestionBankService>();
        services.Configure<MaterialOptions>(configuration.GetSection(MaterialOptions.SectionName));
        services.AddSingleton<IMaterialStorage, LocalMaterialStorage>();
        services.AddScoped<IMaterialService, MaterialService>();
        services.AddScoped<IRubricService, RubricService>();
        services.AddScoped<CreateUserService>();
        services.AddScoped<UpdateUserService>();
        services.AddScoped<ListUsersService>();
        services.AddScoped<GetUserService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IParticipantService, ParticipantService>();
        services.AddScoped<ILecturerAssignmentService, LecturerAssignmentService>();
        services.AddScoped<IAiConfigService, AiConfigService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddOptions<KeycloakAdminOptions>()
            .Bind(configuration.GetSection(KeycloakAdminOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientSecret),
                "KeycloakAdmin:ClientSecret is not configured. Set it with user-secrets or the KeycloakAdmin__ClientSecret environment variable.")
            .ValidateOnStart();
        services.AddHttpClient(KeycloakAdminClient.HttpClientName);
        services.AddSingleton<IKeycloakAdminClient, KeycloakAdminClient>();
        services.AddDataProtection();

        // Public contract for other modules
        services.AddScoped<IExamConfigurationApi, ExamConfigurationApi>();
        return services;
    }
}
