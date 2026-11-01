using System.Net;
using System.Net.Http.Json;
using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiOralExam.IntegrationTests.Http;

public sealed class RubricModeWriteHttpTests
{
    private static readonly Guid LecturerId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222261");

    [Fact]
    public async Task Shared_version_rubric_write_returns_conflict_without_data_or_audit_changes()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(LecturerId, Roles.Lecturer);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var request = new SaveRubricRequest([new(null, 1, "Accuracy", 5m)]);

        using var writeResponse = await client.PutAsJsonAsync(
            $"/api/exam-sessions/{seeded.SessionId}/questions/{seeded.QuestionId}/rubric", request);
        var error = await writeResponse.Content.ReadFromJsonAsync<ApiError>();
        using var readResponse = await client.GetAsync(
            $"/api/exam-sessions/{seeded.SessionId}/questions/{seeded.QuestionId}/rubric");

        Assert.Equal(HttpStatusCode.Conflict, writeResponse.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("rubric_mode_conflict", error.Code);
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        Assert.Empty(await db.Rubrics.ToListAsync());
        Assert.Empty(await db.RubricCriteria.Where(x => x.RubricId != null).ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    private static async Task<Seed> SeedAsync(ApiTestFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var role = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var lecturer = new User
        {
            Id = LecturerId,
            RoleId = role.Id,
            Role = role,
            FullName = "Test Lecturer",
            Email = "rubric-mode@example.test",
            Username = "rubric-mode-lecturer",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "RMW101", Name = "Rubric Mode Course" };
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
            CourseId = course.Id,
            Course = course,
            Name = "Shared rubric",
            CreatedBy = lecturer.Id,
            CreatedAt = DateTime.UtcNow
        };
        var version = new RubricVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            Template = template,
            CourseId = course.Id,
            VersionNo = 1,
            MaxScore = 5m,
            IsSealed = true,
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
        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            RubricMode = RubricMode.SharedVersion,
            RubricVersionId = version.Id,
            RubricVersion = version,
            CreatedBy = lecturer.Id,
            Title = "Shared session",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1),
            TimeLimitPerAnswerSeconds = 120,
            Status = ExamSessionStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };
        var question = new Question
        {
            Id = Guid.NewGuid(),
            ExamSessionId = session.Id,
            OrderNo = 1,
            Content = "Question"
        };
        session.Questions.Add(question);

        db.Roles.Add(role);
        db.Users.Add(lecturer);
        db.Courses.Add(course);
        db.RubricTemplates.Add(template);
        db.RubricVersions.Add(version);
        db.ExamSessions.Add(session);
        await db.SaveChangesAsync();
        return new Seed(session.Id, question.Id);
    }

    private sealed record Seed(Guid SessionId, Guid QuestionId);
}
