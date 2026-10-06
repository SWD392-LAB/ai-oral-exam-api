using AiOralExam.Modules.AccessConfig.Application;
using AiOralExam.Modules.AccessConfig.Application.Auth;
using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiOralExam.Modules.AccessConfig;

public static class AccessConfigModule
{
    public static IServiceCollection AddAccessConfigModule(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AccessConfigDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        // PasswordHasher cua ASP.NET Core Identity: PBKDF2-HMAC-SHA512, salt nam trong hash
        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<IJwtTokenIssuer, JwtTokenIssuer>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IExamSessionQueries, ExamSessionQueries>();

        // Public contract cho module khac
        services.AddScoped<IExamConfigurationApi, ExamConfigurationApi>();
        return services;
    }
}
