using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Application.Audio;
using AiOralExam.Modules.Interview.Application.Ai.Mock;
using AiOralExam.Modules.Interview.Application.Events;
using AiOralExam.Modules.Interview.Application.Features.ScoreReview;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview;

public static class InterviewModule
{
    public static IServiceCollection AddInterviewModule(
        this IServiceCollection services,
        string connectionString,
        IConfiguration configuration,
        string environmentName = "Production")
    {
        services.AddDbContext<InterviewDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.Configure<InterviewOptions>(configuration.GetSection(InterviewOptions.SectionName));

        services.AddScoped<IInterviewService, InterviewService>();
        services.AddScoped<IInterviewAudioSubmissionService, InterviewAudioSubmissionService>();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IAudioChunkStore)))
        {
            if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
                || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase))
                services.AddSingleton<IAudioChunkStore, InMemoryAudioChunkStore>();
            else
                services.AddScoped<IAudioChunkStore, UnavailableAudioChunkStore>();
        }
        services.AddScoped<IInterviewReviewService, InterviewReviewService>();
        services.AddScoped<IInterviewResultsApi, InterviewResultsApi>();
        services.AddSingleton<IInterviewEventPublisher, SignalRInterviewEventPublisher>();
        services.AddHostedService<InterviewAttemptDeadlineWorker>();

        // ---- AI: M1 uses mocks. M2: switch to real adapters here, without touching InterviewService ----
        services.AddSingleton<IAnswerAnalyzer>(sp =>
            new MockAnswerAnalyzer(sp.GetRequiredService<IOptions<InterviewOptions>>().Value.Mock.EnableFollowUps));
        services.AddSingleton<IRubricScorer, MockRubricScorer>();
        services.AddSingleton<ISpeechToTextService, UnavailableSpeechToTextService>();
        services.AddSingleton<ITextToSpeechService, MockTextToSpeechService>();
        services.AddSingleton<ILlmService, MockLlmService>();

        return services;
    }
}
