using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.SharedKernel.Security;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiOralExam.IntegrationTests.Http;

public sealed class InterviewTurnWorkerHttpTests
{
    [Fact]
    public void Attempt_deadline_worker_is_registered_and_running_with_the_api_host()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(Guid.NewGuid(), Roles.Student);
        using var client = factory.CreateClient();

        var worker = Assert.IsAssignableFrom<BackgroundService>(Assert.Single(
            factory.Services.GetServices<IHostedService>(),
            service => service is BackgroundService && service.GetType().Name == "InterviewAttemptDeadlineWorker"));

        Assert.NotNull(worker.ExecuteTask);
        Assert.False(worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Authenticated_client_can_negotiate_the_interview_updates_hub()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(Guid.NewGuid(), Roles.Student);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/hubs/interview/negotiate?negotiateVersion=1", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_client_cannot_negotiate_the_interview_updates_hub()
    {
        using var factory = new ApiTestFactory().AuthenticateAnonymously();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/hubs/interview/negotiate?negotiateVersion=1", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
