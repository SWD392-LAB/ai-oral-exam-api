using AiOralExam.Modules.Interview;
using AiOralExam.Modules.Interview.Application.Audio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiOralExam.UnitTests;

public sealed class InterviewAudioRegistrationTests
{
    [Theory]
    [InlineData("Development", typeof(InMemoryAudioChunkStore))]
    [InlineData("Testing", typeof(InMemoryAudioChunkStore))]
    [InlineData("Production", typeof(UnavailableAudioChunkStore))]
    public void Audio_store_selection_is_safe_for_the_environment(string environmentName, Type expectedType)
    {
        var services = new ServiceCollection();
        services.AddInterviewModule("Host=localhost;Database=test", new ConfigurationBuilder().Build(), environmentName);
        using var provider = services.BuildServiceProvider();

        Assert.IsType(expectedType, provider.GetRequiredService<IAudioChunkStore>());
    }

    [Fact]
    public void Explicit_audio_store_registration_is_preserved_in_production()
    {
        var configuredStore = new InMemoryAudioChunkStore();
        var services = new ServiceCollection();
        services.AddSingleton<IAudioChunkStore>(configuredStore);
        services.AddInterviewModule("Host=localhost;Database=test", new ConfigurationBuilder().Build(), Environments.Production);
        using var provider = services.BuildServiceProvider();

        Assert.Same(configuredStore, provider.GetRequiredService<IAudioChunkStore>());
    }
}
