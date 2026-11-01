using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiOralExam.IntegrationTests.Http;

public sealed class QuestionGenerationHttpContractTests
{
    [Fact]
    public async Task Generation_endpoint_persists_source_verified_drafts_as_unapproved_bank_items()
    {
        var lecturerId = Guid.NewGuid();
        var courseId = Guid.NewGuid();
        var sourceText = "Retrieval selects course-scoped evidence for generated questions.";
        using var factory = new ApiTestFactory().AuthenticateAs(lecturerId, Roles.Lecturer);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
            db.Courses.Add(new Course
            {
                Id = courseId,
                Code = "SWD392",
                Name = "Software Design",
                Lecturers = [new CourseLecturer { CourseId = courseId, LecturerId = lecturerId }]
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.PostAsJsonAsync($"/api/courses/{courseId}/question-bank/generations", new
        {
            topic = "retrieval",
            bloom = "Analyze",
            count = 1,
            sourceExcerpts = new[] { sourceText },
            platformRubricVersionId = Guid.NewGuid()
        });
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(responseBody);
        var draft = document.RootElement.GetProperty("drafts")[0];
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Contains(draft.GetProperty("citations")[0].GetProperty("quote").GetString()!, sourceText);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var bankItem = await verifyDb.QuestionBankItems.Include(x => x.Revisions).SingleAsync();
        Assert.Equal(QuestionBankOrigin.Ai, bankItem.Origin);
        Assert.Equal(QuestionBankStatus.Draft, bankItem.Status);
        Assert.Contains(sourceText, bankItem.Revisions.Single().ProvenanceJson);
    }
}
