using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.RubricVersions;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AiOralExam.IntegrationTests.Http;

public sealed class PlatformRubricHttpTests
{
    private static readonly Guid ActorId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222251");

    [Theory]
    [InlineData(Roles.Lecturer)]
    [InlineData(Roles.Administrator)]
    public async Task Lecturer_or_administrator_can_get_the_active_platform_rubric(string role)
    {
        using var factory = new ApiTestFactory().AuthenticateAs(ActorId, role);
        var expectedVersionId = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/platform-rubric");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var version = await response.Content.ReadFromJsonAsync<RubricVersionDetailDto>();
        Assert.NotNull(version);
        Assert.Equal(expectedVersionId, version.Id);
        Assert.Null(version.CourseId);
        Assert.True(version.IsActive);
        Assert.True(version.IsSealed);
        Assert.Equal(new[] { 1, 2 }, version.Criteria.Select(x => x.OrderNo));
        Assert.Equal(new[] { "Accuracy", "Clarity" }, version.Criteria.Select(x => x.Description));
    }

    [Fact]
    public async Task Student_receives_forbidden_for_platform_rubric()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(ActorId, Roles.Student);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/platform-rubric");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_receives_unauthorized_for_platform_rubric()
    {
        using var factory = new ApiTestFactory().AuthenticateAnonymously();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/platform-rubric");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task No_active_platform_rubric_returns_not_found_with_error_code()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(ActorId, Roles.Lecturer);
        await SeedAsync(factory, "none");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/platform-rubric");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("active_platform_rubric_not_found", error.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unsealed")]
    [InlineData("course-scoped")]
    public async Task Nonqualifying_version_scenarios_return_not_found(string scenario)
    {
        using var factory = new ApiTestFactory().AuthenticateAs(ActorId, Roles.Lecturer);
        await SeedAsync(factory, scenario);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/platform-rubric");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("active_platform_rubric_not_found", error.RootElement.GetProperty("code").GetString());
    }

    private static async Task<Guid> SeedAsync(ApiTestFactory factory, string scenario = "active")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var role = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var user = new User
        {
            Id = ActorId,
            RoleId = role.Id,
            Role = role,
            FullName = "Platform Rubric Lecturer",
            Email = "platform-rubric@example.test",
            Username = "platform-rubric-lecturer",
            LecturerCode = "platform-rubric",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "PLAT101", Name = "Platform Test Course" };
        var platformTemplate = new RubricTemplate
        {
            Id = Guid.NewGuid(), Name = "Platform rubric", CreatedBy = user.Id, CreatedAt = DateTime.UtcNow
        };
        var courseTemplate = new RubricTemplate
        {
            Id = Guid.NewGuid(), CourseId = course.Id, Course = course, Name = "Course rubric",
            CreatedBy = user.Id, CreatedAt = DateTime.UtcNow
        };

        var versions = new List<RubricVersion>();
        if (scenario != "none")
        {
            var template = scenario == "course-scoped" ? courseTemplate : platformTemplate;
            var version = NewVersion(
                template,
                user.Id,
                versionNo: 1,
                isSealed: scenario != "unsealed",
                isActive: scenario != "inactive");
            versions.Add(version);
            if (version.IsSealed)
            {
                if (scenario == "active")
                {
                    db.RubricCriteria.AddRange(
                        new RubricCriterion
                        {
                            Id = Guid.NewGuid(), RubricVersionId = version.Id, RubricVersion = version,
                            OrderNo = 2, Description = "Clarity", MaxPoints = 5m
                        },
                        new RubricCriterion
                        {
                            Id = Guid.NewGuid(), RubricVersionId = version.Id, RubricVersion = version,
                            OrderNo = 1, Description = "Accuracy", MaxPoints = 5m
                        });
                }
                else
                {
                    db.RubricCriteria.Add(new RubricCriterion
                    {
                        Id = Guid.NewGuid(), RubricVersionId = version.Id, RubricVersion = version,
                        OrderNo = 1, Description = "Criterion", MaxPoints = 10m
                    });
                }
            }
        }

        db.Roles.Add(role);
        db.Users.Add(user);
        db.Courses.Add(course);
        db.RubricTemplates.AddRange(platformTemplate, courseTemplate);
        db.RubricVersions.AddRange(versions);
        await db.SaveChangesAsync();
        return versions.SingleOrDefault()?.Id ?? Guid.Empty;
    }

    private static RubricVersion NewVersion(
        RubricTemplate template, Guid createdBy, int versionNo, bool isSealed, bool isActive) => new()
    {
        Id = Guid.NewGuid(),
        TemplateId = template.Id,
        Template = template,
        CourseId = template.CourseId,
        VersionNo = versionNo,
        MaxScore = 10m,
        IsSealed = isSealed,
        IsActive = isActive,
        CreatedBy = createdBy,
        CreatedAt = DateTime.UtcNow
    };
}
