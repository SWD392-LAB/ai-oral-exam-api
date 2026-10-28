using System.Net;
using System.Net.Http.Json;
using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.QuestionBank;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AiOralExam.IntegrationTests.Http;

public sealed class QuestionBankHttpContractTests
{
    [Fact]
    public async Task Lecturer_can_create_import_approve_and_attach_a_question_bank_item()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(Ids.Lecturer, Roles.Lecturer);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var createResponse = await client.PostAsJsonAsync(
            $"/api/courses/{Ids.Course}/question-bank",
            new { topic = "Databases", bloom = "Understand", content = "Explain a relational database." });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<QuestionBankItemDto>();
        Assert.NotNull(created);
        Assert.EndsWith($"/api/courses/{Ids.Course}/question-bank/{created.Id}",
            createResponse.Headers.Location?.OriginalString);
        Assert.Equal("Draft", created.Status);
        Assert.Equal(1, Assert.Single(created.Revisions).RevisionNo);

        using var approveResponse = await client.PostAsync(
            $"/api/courses/{Ids.Course}/question-bank/{created.Id}/approve", content: null);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var approved = await approveResponse.Content.ReadFromJsonAsync<QuestionBankItemDto>();
        Assert.NotNull(approved);
        Assert.Equal("Approved", approved.Status);

        using var attachResponse = await client.PostAsJsonAsync(
            $"/api/exam-sessions/{Ids.Session}/questions/from-bank",
            new { itemIds = new[] { created.Id } });
        Assert.Equal(HttpStatusCode.OK, attachResponse.StatusCode);
        Assert.Equal(new[] { created.Id }, await attachResponse.Content.ReadFromJsonAsync<Guid[]>());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var question = Assert.Single(db.Questions);
        Assert.Equal("Explain a relational database.", question.Content);
        Assert.Equal(created.Revisions[0].Id, question.SourceBankRevisionId);
        Assert.Equal(3, db.AuditLogs.Count());
    }

    [Fact]
    public async Task Import_validation_is_row_specific_and_persists_no_partial_batch()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(Ids.Lecturer, Roles.Lecturer);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/courses/{Ids.Course}/question-bank/import",
            new
            {
                items = new[]
                {
                    new { topic = "Databases", bloom = "Remember", content = "Valid question" },
                    new { topic = " ", bloom = "Analyze", content = " " }
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("validation_failed", error.Code);
        Assert.Contains("items[1].content", error.Details!.Keys);
        Assert.Contains("items[1].topic", error.Details.Keys);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        Assert.Empty(db.QuestionBankItems);
        Assert.Empty(db.QuestionBankRevisions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Lecturer_cannot_list_or_create_items_for_an_unassigned_course()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(Ids.Lecturer, Roles.Lecturer);
        var otherCourseId = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var listResponse = await client.GetAsync($"/api/courses/{otherCourseId}/question-bank");
        using var createResponse = await client.PostAsJsonAsync(
            $"/api/courses/{otherCourseId}/question-bank",
            new { topic = "Topic", bloom = "Remember", content = "Question" });

        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);
    }

    private static async Task<Guid> SeedAsync(ApiTestFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var role = new Role { Id = Ids.LecturerRole, Name = Roles.Lecturer };
        var lecturer = new User
        {
            Id = Ids.Lecturer,
            RoleId = role.Id,
            Role = role,
            FullName = "Question Bank Lecturer",
            Email = "question-bank-lecturer@example.test",
            Username = "question-bank-lecturer",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course { Id = Ids.Course, Code = "QB101", Name = "Question Bank Course" };
        var otherCourse = new Course { Id = Guid.NewGuid(), Code = "QB202", Name = "Other Course" };
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = lecturer.Id,
            Lecturer = lecturer
        });
        db.Roles.Add(role);
        db.Users.Add(lecturer);
        db.Courses.AddRange(course, otherCourse);
        db.ExamSessions.Add(new ExamSession
        {
            Id = Ids.Session,
            CourseId = course.Id,
            Course = course,
            CreatedBy = lecturer.Id,
            Title = "Draft session",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1),
            Status = ExamSessionStatus.Draft,
            RubricMode = RubricMode.Legacy
        });
        await db.SaveChangesAsync();
        return otherCourse.Id;
    }

    private static class Ids
    {
        public static readonly Guid Lecturer = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222281");
        public static readonly Guid LecturerRole = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222282");
        public static readonly Guid Course = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222283");
        public static readonly Guid Session = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222284");
    }
}
