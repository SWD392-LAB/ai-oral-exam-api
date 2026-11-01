using System.Net;
using AiOralExam.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiOralExam.IntegrationTests.Http;

public sealed class SmokeTests
{
    [Fact]
    public async Task Root_endpoint_redirects_to_swagger()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/swagger", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Swagger_document_contains_question_generation_and_audio_routes()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        var document = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {document}");
        Assert.Contains("/api/courses/{courseId}/question-bank/generations", document);
        Assert.Contains("/api/interview-attempts/{id}/audio", document);
    }
}
