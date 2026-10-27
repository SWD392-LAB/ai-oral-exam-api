using System.Net;
using System.Net.Http.Json;
using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.RubricTemplates;
using AiOralExam.Modules.AccessConfig.Application.RubricVersions;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiOralExam.IntegrationTests.Http;

public sealed class RubricVersionHttpContractTests
{
    private static readonly Guid LecturerId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222241");
    private static readonly Guid OtherLecturerId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222242");

    [Fact]
    public async Task Administrator_can_create_list_and_get_sealed_version()
    {
        using var factory = NewFactory();
        var seeded = await SeedAsync(factory, assignedToActor: true);
        using var client = factory.CreateClient();

        using var createResponse = await client.PostAsJsonAsync(
            $"/api/rubric-templates/{seeded.TemplateId}/versions", ValidRequest());

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<RubricVersionDetailDto>();
        Assert.NotNull(created);
        Assert.Equal(1, created.VersionNo);
        Assert.True(created.IsSealed);
        Assert.Equal(10m, created.MaxScore);
        Assert.Equal(new[] { 1, 2 }, created.Criteria.Select(x => x.OrderNo));
        Assert.All(created.Criteria, x => Assert.NotEqual(Guid.Empty, x.Id));
        Assert.EndsWith($"/api/rubric-templates/{seeded.TemplateId}/versions/{created.Id}",
            createResponse.Headers.Location?.OriginalString);

        var list = await client.GetFromJsonAsync<List<RubricVersionDto>>(
            $"/api/rubric-templates/{seeded.TemplateId}/versions");
        Assert.NotNull(list);
        Assert.Equal(created.Id, Assert.Single(list).Id);

        var detail = await client.GetFromJsonAsync<RubricVersionDetailDto>(
            $"/api/rubric-templates/{seeded.TemplateId}/versions/{created.Id}");
        Assert.NotNull(detail);
        Assert.Equal(created.Criteria.Select(x => x.Id), detail.Criteria.Select(x => x.Id));
    }

    [Fact]
    public async Task Lecturer_can_read_global_rubric_but_cannot_manage_or_activate_it()
    {
        using var factory = NewFactory();
        await SeedAsync(factory, assignedToActor: true);
        Guid templateId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
            var template = new RubricTemplate
            {
                Id = Guid.NewGuid(),
                Name = "Platform rubric",
                CreatedBy = LecturerId,
                CreatedAt = DateTime.UtcNow
            };
            db.RubricTemplates.Add(template);
            await db.SaveChangesAsync();
            templateId = template.Id;
        }

        using var client = factory.CreateClient();
        using var createResponse = await client.PostAsJsonAsync(
            $"/api/rubric-templates/{templateId}/versions", ValidRequest());
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var version = await createResponse.Content.ReadFromJsonAsync<RubricVersionDetailDto>();
        Assert.NotNull(version);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
            var stored = await db.RubricVersions.SingleAsync(x => x.Id == version.Id);
            stored.MaxScore = version.MaxScore;
            await db.SaveChangesAsync();
        }
        using var activateResponse = await client.PostAsync(
            $"/api/rubric-templates/{templateId}/versions/{version.Id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);

        factory.AuthenticateAs(LecturerId, Roles.Lecturer);
        using var readTemplateResponse = await client.GetAsync($"/api/rubric-templates/{templateId}");
        using var readVersionResponse = await client.GetAsync(
            $"/api/rubric-templates/{templateId}/versions/{version.Id}");
        using var activateAsLecturerResponse = await client.PostAsync(
            $"/api/rubric-templates/{templateId}/versions/{version.Id}/activate", null);
        using var createAsLecturerResponse = await client.PostAsJsonAsync(
            $"/api/rubric-templates/{templateId}/versions", ValidRequest());

        Assert.Equal(HttpStatusCode.OK, readTemplateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readVersionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, activateAsLecturerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, createAsLecturerResponse.StatusCode);
        Assert.Null((await readTemplateResponse.Content.ReadFromJsonAsync<RubricTemplateDto>())?.CourseId);
    }

    [Fact]
    public async Task Unassigned_lecturer_cannot_create_or_read_versions()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(LecturerId, Roles.Lecturer);
        var seeded = await SeedAsync(factory, assignedToActor: false, addVersion: true, courseScopedTemplate: true);
        using var client = factory.CreateClient();

        using var createResponse = await client.PostAsJsonAsync(
            $"/api/rubric-templates/{seeded.TemplateId}/versions", ValidRequest());
        using var listResponse = await client.GetAsync($"/api/rubric-templates/{seeded.TemplateId}/versions");
        using var detailResponse = await client.GetAsync(
            $"/api/rubric-templates/{seeded.TemplateId}/versions/{seeded.VersionId}");

        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, detailResponse.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var version = await db.RubricVersions.SingleAsync(x => x.Id == seeded.VersionId);
        Assert.Equal(1, version.VersionNo);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Empty_criteria_are_rejected_and_version_template_mismatch_is_not_found()
    {
        using var factory = NewFactory();
        var seeded = await SeedAsync(factory, assignedToActor: true, addSecondTemplate: true);
        using var client = factory.CreateClient();

        using var invalidResponse = await client.PostAsJsonAsync(
            $"/api/rubric-templates/{seeded.TemplateId}/versions", new { criteria = Array.Empty<object>() });
        using var createResponse = await client.PostAsJsonAsync(
            $"/api/rubric-templates/{seeded.TemplateId}/versions", ValidRequest());
        var created = await createResponse.Content.ReadFromJsonAsync<RubricVersionDetailDto>();
        Assert.NotNull(created);
        using var mismatchResponse = await client.GetAsync(
            $"/api/rubric-templates/{seeded.OtherTemplateId}/versions/{created.Id}");

        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, mismatchResponse.StatusCode);
    }

    private static ApiTestFactory NewFactory() => new ApiTestFactory().AuthenticateAs(LecturerId, Roles.Administrator);

    private static CreateRubricVersionRequest ValidRequest() => new(
    [
        new(1, "Accuracy", 5m),
        new(2, "Clarity", 5m)
    ]);

    private static async Task<SeededTemplates> SeedAsync(
        ApiTestFactory factory, bool assignedToActor, bool addSecondTemplate = false, bool addVersion = false,
        bool courseScopedTemplate = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var lecturerRole = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var actor = NewUser(LecturerId, lecturerRole);
        var other = NewUser(OtherLecturerId, lecturerRole);
        var course = new Course { Id = Guid.NewGuid(), Code = "VER101", Name = "Version Course" };
        var assignedLecturer = assignedToActor ? actor : other;
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = assignedLecturer.Id,
            Lecturer = assignedLecturer,
            AssignedAt = DateTime.UtcNow
        });
        var template = courseScopedTemplate
            ? NewTemplate(actor.Id, course, "Main template")
            : new RubricTemplate
            {
                Id = Guid.NewGuid(), Name = "Main template", CreatedBy = actor.Id, CreatedAt = DateTime.UtcNow
            };
        var otherTemplate = addSecondTemplate ? NewTemplate(actor.Id, course, "Other template") : null;
        var version = addVersion
            ? new RubricVersion
            {
                Id = Guid.NewGuid(),
                TemplateId = template.Id,
                Template = template,
                CourseId = template.CourseId,
                VersionNo = 1,
                MaxScore = 10m,
                IsSealed = true,
                CreatedBy = actor.Id,
                CreatedAt = DateTime.UtcNow
            }
            : null;

        db.Roles.Add(lecturerRole);
        db.Users.AddRange(actor, other);
        db.Courses.Add(course);
        db.RubricTemplates.Add(template);
        if (otherTemplate is not null)
            db.RubricTemplates.Add(otherTemplate);
        if (version is not null)
            db.RubricVersions.Add(version);
        await db.SaveChangesAsync();

        return new SeededTemplates(template.Id, otherTemplate?.Id ?? template.Id, version?.Id);
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

    private static RubricTemplate NewTemplate(Guid creatorId, Course course, string name) => new()
    {
        Id = Guid.NewGuid(),
        CourseId = course.Id,
        Course = course,
        Name = name,
        CreatedBy = creatorId,
        CreatedAt = DateTime.UtcNow
    };

    private sealed record SeededTemplates(Guid TemplateId, Guid OtherTemplateId, Guid? VersionId);
}
