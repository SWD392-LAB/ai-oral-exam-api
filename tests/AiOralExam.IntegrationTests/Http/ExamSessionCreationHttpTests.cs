using System.Net;
using System.Net.Http.Json;
using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AiOralExam.IntegrationTests.Http;

public sealed class ExamSessionCreationHttpTests
{
    private static readonly Guid LecturerId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222251");

    [Fact]
    public async Task Create_preserves_legacy_request_and_returns_selection_for_legacy_and_shared_details()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(LecturerId, Roles.Lecturer);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var start = DateTime.UtcNow.AddHours(1);
        var end = start.AddHours(1);

        using var legacyResponse = await client.PostAsJsonAsync("/api/exam-sessions", new
        {
            courseId = seeded.CourseId,
            title = "Legacy session",
            startTime = start,
            endTime = end,
            timeLimitPerAnswerSeconds = 120,
            maxFollowUps = 2,
            rubricMode = "Legacy"
        });

        Assert.Equal(HttpStatusCode.Created, legacyResponse.StatusCode);
        var legacy = await legacyResponse.Content.ReadFromJsonAsync<ExamSessionCommandDto>();
        Assert.NotNull(legacy);
        Assert.Equal("Legacy", legacy.RubricMode);
        Assert.Null(legacy.RubricVersionId);

        using var legacyDetailResponse = await client.GetAsync($"/api/exam-sessions/{legacy.Id}");
        Assert.Equal(HttpStatusCode.OK, legacyDetailResponse.StatusCode);
        var legacyDetail = await legacyDetailResponse.Content.ReadFromJsonAsync<ExamSessionDetailDto>();
        Assert.NotNull(legacyDetail);
        Assert.Equal("Legacy", legacyDetail.RubricMode);
        Assert.Null(legacyDetail.RubricVersionId);

        using var defaultResponse = await client.PostAsJsonAsync("/api/exam-sessions", new
        {
            courseId = seeded.CourseId,
            title = "Default shared session",
            startTime = start,
            endTime = end,
            timeLimitPerAnswerSeconds = 120,
            maxFollowUps = 2
        });

        Assert.Equal(HttpStatusCode.Created, defaultResponse.StatusCode);
        var defaultSession = await defaultResponse.Content.ReadFromJsonAsync<ExamSessionCommandDto>();
        Assert.NotNull(defaultSession);
        Assert.Equal("SharedVersion", defaultSession.RubricMode);
        Assert.Equal(seeded.VersionId, defaultSession.RubricVersionId);

        using var sharedResponse = await client.PostAsJsonAsync("/api/exam-sessions", new
        {
            courseId = seeded.CourseId,
            title = "Shared session",
            startTime = start,
            endTime = end,
            timeLimitPerAnswerSeconds = 120,
            maxFollowUps = 2,
            rubricMode = "SharedVersion",
            rubricVersionId = seeded.VersionId
        });

        Assert.Equal(HttpStatusCode.Created, sharedResponse.StatusCode);
        var shared = await sharedResponse.Content.ReadFromJsonAsync<ExamSessionCommandDto>();
        Assert.NotNull(shared);
        Assert.Equal("SharedVersion", shared.RubricMode);
        Assert.Equal(seeded.VersionId, shared.RubricVersionId);

        using var sharedDetailResponse = await client.GetAsync($"/api/exam-sessions/{shared.Id}");
        Assert.Equal(HttpStatusCode.OK, sharedDetailResponse.StatusCode);
        var sharedDetail = await sharedDetailResponse.Content.ReadFromJsonAsync<ExamSessionDetailDto>();
        Assert.NotNull(sharedDetail);
        Assert.Equal("SharedVersion", sharedDetail.RubricMode);
        Assert.Equal(seeded.VersionId, sharedDetail.RubricVersionId);
    }

    private static async Task<Seed> SeedAsync(ApiTestFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var lecturerRole = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var lecturer = new User
        {
            Id = LecturerId,
            RoleId = lecturerRole.Id,
            Role = lecturerRole,
            FullName = "Test Lecturer",
            Email = "session-creation@example.test",
            Username = "session-creation-lecturer",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "SC101", Name = "Selection Course" };
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = lecturer.Id,
            Lecturer = lecturer,
            AssignedAt = DateTime.UtcNow
        });
        var template = new RubricTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Shared rubric",
            CreatedBy = lecturer.Id,
            CreatedAt = DateTime.UtcNow
        };
        var version = new RubricVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            Template = template,
            CourseId = null,
            VersionNo = 1,
            MaxScore = 5m,
            IsSealed = true,
            IsActive = true,
            CreatedBy = lecturer.Id,
            CreatedAt = DateTime.UtcNow
        };
        version.Criteria.Add(new RubricCriterion
        {
            Id = Guid.NewGuid(),
            RubricVersionId = version.Id,
            RubricVersion = version,
            OrderNo = 1,
            Description = "Accuracy",
            MaxPoints = 5m
        });

        db.Roles.Add(lecturerRole);
        db.Users.Add(lecturer);
        db.Courses.Add(course);
        db.RubricTemplates.Add(template);
        db.RubricVersions.Add(version);
        await db.SaveChangesAsync();
        return new Seed(course.Id, version.Id);
    }

    private sealed record Seed(Guid CourseId, Guid VersionId);
}
