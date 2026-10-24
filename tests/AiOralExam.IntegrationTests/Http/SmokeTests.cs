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
}
