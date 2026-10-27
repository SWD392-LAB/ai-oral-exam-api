using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Application.Ai.Mock;
using AiOralExam.Modules.Interview.Application.Audio;
using AiOralExam.Modules.Interview.Application.Features.ScoreReview;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview;

public static class InterviewModule
{
    public static IServiceCollection AddInterviewModule(this IServiceCollection services, string connectionString, IConfiguration configuration)
    {
        services.AddDbContext<InterviewDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.Configure<InterviewOptions>(configuration.GetSection(InterviewOptions.SectionName));
        services.Configure<AiProviderOptions>(configuration.GetSection(AiProviderOptions.SectionName));

        services.AddScoped<IInterviewService, InterviewService>();
        services.AddScoped<IInterviewReviewService, InterviewReviewService>();
        services.AddScoped<IInterviewResultsApi, InterviewResultsApi>();

        // ---- AI: mocks remain the safe default; real adapters are opt-in through AiProviders ----
        services.AddSingleton<MockAnswerAnalyzer>(sp =>
            new MockAnswerAnalyzer(sp.GetRequiredService<IOptions<InterviewOptions>>().Value.Mock.EnableFollowUps));
        services.AddSingleton<MockRubricScorer>();
        services.AddSingleton<MockLlmService>();
        services.AddSingleton<MockSpeechToTextService>();
        services.AddSingleton<MockTextToSpeechService>();
        services.AddHttpClient<GeminiLlmService>((sp, client) =>
        {
            var seconds = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value.RequestTimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));
        });
        services.AddHttpClient<LocalHttpSpeechToTextService>((sp, client) =>
        {
            var seconds = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value.RequestTimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));
        });
        services.AddHttpClient<LocalHttpTextToSpeechService>((sp, client) =>
        {
            var seconds = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value.RequestTimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));
        });
        services.AddScoped<GeminiAnswerAnalyzer>();
        services.AddScoped<GeminiRubricScorer>();
        services.AddScoped<ILlmService>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value;
            return settings.UsesGemini
                ? sp.GetRequiredService<GeminiLlmService>()
                : sp.GetRequiredService<MockLlmService>();
        });
        services.AddScoped<IAnswerAnalyzer>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value;
            IAnswerAnalyzer inner = settings.UsesGemini
                ? sp.GetRequiredService<GeminiAnswerAnalyzer>()
                : sp.GetRequiredService<MockAnswerAnalyzer>();
            return new ValidatedAnswerAnalyzer(inner, sp.GetRequiredService<ILogger<ValidatedAnswerAnalyzer>>());
        });
        services.AddScoped<IRubricScorer>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value;
            IRubricScorer inner = settings.UsesGemini
                ? sp.GetRequiredService<GeminiRubricScorer>()
                : sp.GetRequiredService<MockRubricScorer>();
            return new ValidatedRubricScorer(inner, sp.GetRequiredService<ILogger<ValidatedRubricScorer>>());
        });
        services.AddScoped<ISpeechToTextService>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value;
            return settings.UsesLocalStt
                ? sp.GetRequiredService<LocalHttpSpeechToTextService>()
                : sp.GetRequiredService<MockSpeechToTextService>();
        });
        services.AddScoped<ITextToSpeechService>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value;
            return settings.UsesLocalTts
                ? sp.GetRequiredService<LocalHttpTextToSpeechService>()
                : sp.GetRequiredService<MockTextToSpeechService>();
        });
        services.AddSingleton<IAudioStorage, LocalAudioStorage>();
        services.AddScoped<IInterviewAudioService, InterviewAudioService>();

        return services;
    }
}
