using System.Net;
using System.Net.Http.Json;
using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.RubricTemplates;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiOralExam.IntegrationTests.Http;

public sealed class RubricTemplateHttpContractTests
{
    private static readonly Guid ActorId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222231");
    private static readonly Guid OtherLecturerId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222232");

    [Fact]
    public async Task Assigned_lecturer_can_create_list_get_update_and_delete_template_without_versions()
    {
        using var factory = NewFactory();
        var course = await SeedAsync(factory, assignedToActor: true);
        using var client = factory.CreateClient();

        using var createResponse = await client.PostAsJsonAsync("/api/rubric-templates", new
        {
            courseId = course.Id,
            name = "Oral rubric"
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<RubricTemplateDto>();
        Assert.NotNull(created);
        Assert.Equal(ActorId, created.CreatedBy);
        Assert.Equal(course.Id, created.CourseId);
        Assert.EndsWith($"/api/rubric-templates/{created.Id}", createResponse.Headers.Location?.OriginalString);

        var list = await client.GetFromJsonAsync<List<RubricTemplateDto>>(
            $"/api/rubric-templates?courseId={course.Id}");
        Assert.NotNull(list);
        Assert.Equal(created.Id, Assert.Single(list).Id);

        var detail = await client.GetFromJsonAsync<RubricTemplateDto>($"/api/rubric-templates/{created.Id}");
        Assert.NotNull(detail);
        Assert.Equal("Oral rubric", detail.Name);

        using var updateResponse = await client.PatchAsJsonAsync($"/api/rubric-templates/{created.Id}", new
        {
            name = "Updated oral rubric"
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<RubricTemplateDto>();
        Assert.NotNull(updated);
        Assert.Equal("Updated oral rubric", updated.Name);
        Assert.Equal(ActorId, updated.CreatedBy);

        using var deleteResponse = await client.DeleteAsync($"/api/rubric-templates/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        using var missingResponse = await client.GetAsync($"/api/rubric-templates/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public async Task Lecturer_cannot_access_templates_for_unassigned_course()
    {
        using var factory = NewFactory();
        var course = await SeedAsync(factory, assignedToActor: false, addTemplate: true);
        using var client = factory.CreateClient();

        using var createResponse = await client.PostAsJsonAsync("/api/rubric-templates", new
        {
            courseId = course.Id,
            name = "Not allowed"
        });
        using var detailResponse = await client.GetAsync($"/api/rubric-templates/{course.TemplateId}");
        using var updateResponse = await client.PatchAsJsonAsync(
            $"/api/rubric-templates/{course.TemplateId}", new { name = "Unauthorized rename" });
        using var deleteResponse = await client.DeleteAsync($"/api/rubric-templates/{course.TemplateId}");
        var list = await client.GetFromJsonAsync<List<RubricTemplateDto>>(
            $"/api/rubric-templates?courseId={course.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, detailResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);
        Assert.NotNull(list);
        Assert.Empty(list);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var unchanged = await db.RubricTemplates.SingleAsync(x => x.Id == course.TemplateId);
        Assert.Equal("Existing template", unchanged.Name);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Student_cannot_access_template_or_version_routes_even_with_course_assignment()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(ActorId, Roles.Student);
        var course = await SeedAsync(factory, assignedToActor: true, addTemplate: true, addVersion: true);
        using var client = factory.CreateClient();
        var templateId = course.TemplateId!.Value;
        var body = new { criteria = new[] { new { orderNo = 1, description = "Accuracy", maxPoints = 5m } } };

        var responses = new[]
        {
            await client.PostAsJsonAsync("/api/rubric-templates", new { courseId = course.Id, name = "Student" }),
            await client.GetAsync("/api/rubric-templates"),
            await client.GetAsync($"/api/rubric-templates/{templateId}"),
            await client.PatchAsJsonAsync($"/api/rubric-templates/{templateId}", new { name = "Changed" }),
            await client.DeleteAsync($"/api/rubric-templates/{templateId}"),
            await client.PostAsJsonAsync($"/api/rubric-templates/{templateId}/versions", body),
            await client.GetAsync($"/api/rubric-templates/{templateId}/versions"),
            await client.GetAsync($"/api/rubric-templates/{templateId}/versions/{Guid.NewGuid()}")
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        foreach (var response in responses)
            response.Dispose();
    }

    [Fact]
    public async Task Invalid_name_returns_bad_request_and_template_with_versions_cannot_be_deleted()
    {
        using var factory = NewFactory();
        var course = await SeedAsync(factory, assignedToActor: true, addTemplate: true, addVersion: true);
        using var client = factory.CreateClient();

        using var invalidResponse = await client.PostAsJsonAsync("/api/rubric-templates", new
        {
            courseId = course.Id,
            name = "   "
        });
        using var deleteResponse = await client.DeleteAsync($"/api/rubric-templates/{course.TemplateId}");

        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        Assert.NotNull(await db.RubricTemplates.FindAsync(course.TemplateId));
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    private static ApiTestFactory NewFactory() => new ApiTestFactory().AuthenticateAs(ActorId, Roles.Lecturer);

    private static async Task<SeededCourse> SeedAsync(
        ApiTestFactory factory,
        bool assignedToActor,
        bool addTemplate = false,
        bool addVersion = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var lecturerRole = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var actor = NewUser(ActorId, lecturerRole);
        var otherLecturer = NewUser(OtherLecturerId, lecturerRole);
        var course = new Course { Id = Guid.NewGuid(), Code = $"C{Guid.NewGuid():N}"[..20], Name = "Test Course" };
        var assignedLecturer = assignedToActor ? actor : otherLecturer;
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = assignedLecturer.Id,
            Lecturer = assignedLecturer,
            AssignedAt = DateTime.UtcNow
        });
        RubricTemplate? template = null;
        if (addTemplate)
        {
            template = new RubricTemplate
            {
                Id = Guid.NewGuid(),
                CourseId = course.Id,
                Course = course,
                Name = "Existing template",
                CreatedBy = actor.Id,
                CreatedAt = DateTime.UtcNow
            };
        }

        db.Roles.Add(lecturerRole);
        db.Users.AddRange(actor, otherLecturer);
        db.Courses.Add(course);
        if (template is not null)
        {
            db.RubricTemplates.Add(template);
            if (addVersion)
                db.RubricVersions.Add(new RubricVersion
                {
                    Id = Guid.NewGuid(),
                    TemplateId = template.Id,
                    Template = template,
                    CourseId = course.Id,
                    VersionNo = 1,
                    MaxScore = 10m,
                    IsSealed = true,
                    CreatedBy = actor.Id,
                    CreatedAt = DateTime.UtcNow
                });
        }
        await db.SaveChangesAsync();
        return new SeededCourse(course.Id, template?.Id);
    }

    private static User NewUser(Guid id, Role role) => new()
    {
        Id = id,
        RoleId = role.Id,
        Role = role,
        FullName = "Test Lecturer",
        Email = $"{id:N}@example.test",
        Username = id.ToString("N"),
        LecturerCode = id.ToString("N")[..20],
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };

    private sealed record SeededCourse(Guid Id, Guid? TemplateId);
}
