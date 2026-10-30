using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.SharedKernel.Security;
using System.Net;
using System.Net.Http.Json;

namespace AiOralExam.IntegrationTests.Http;

public sealed class InterviewRecoveryHttpContractTests
{
    [Fact]
    public async Task Anonymous_recovery_request_is_rejected_before_application_service()
    {
        using var factory = new ApiTestFactory().AuthenticateAnonymously();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/interview-attempts/{Guid.NewGuid()}/recovery",
            new RecoverInterruptedTurnRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Student_recovery_request_is_rejected_by_the_http_role_policy()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(Guid.NewGuid(), Roles.Student);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/interview-attempts/{Guid.NewGuid()}/recovery",
            new RecoverInterruptedTurnRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Lecturer_recovery_request_reaches_application_eligibility_checks()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(Guid.NewGuid(), Roles.Lecturer);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/interview-attempts/{Guid.NewGuid()}/recovery",
            new RecoverInterruptedTurnRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Empty_turn_id_is_rejected_by_the_recovery_contract()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(Guid.NewGuid(), Roles.Administrator);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/interview-attempts/{Guid.NewGuid()}/recovery",
            new RecoverInterruptedTurnRequest(Guid.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
