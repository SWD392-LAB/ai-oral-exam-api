using AiOralExam.Modules.AccessConfig.Application;
using AiOralExam.Modules.AccessConfig.Application.AIServices;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Auth;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.Modules.AccessConfig.Application.Keycloak;
using AiOralExam.Modules.AccessConfig.Application.LecturerAssignments;
using AiOralExam.Modules.AccessConfig.Application.Participants;
using AiOralExam.Modules.AccessConfig.Application.Questions;
using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.AccessConfig.Application.Users;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddScoped<ICourseService, CourseService>();
        services.AddScoped<IQuestionService, QuestionService>();
        services.AddScoped<IRubricService, RubricService>();
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
