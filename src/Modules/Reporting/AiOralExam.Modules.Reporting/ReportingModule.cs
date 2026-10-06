using AiOralExam.Modules.Reporting.Application;
using Microsoft.Extensions.DependencyInjection;

namespace AiOralExam.Modules.Reporting;

public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        services.AddScoped<IReportService, ReportService>();
        return services;
    }
}
