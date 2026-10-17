using AiOralExam.Modules.AccessConfig.Application.Questions;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiOralExam.UnitTests;

public class QuestionImportTests
{
    [Fact]
    public async Task Create_persists_one_audit_row_with_the_question()
    {
        await using var db = CreateDbContext();
        var (sessionId, lecturer) = await AddSessionAsync(db);
        var service = new QuestionService(db, new TestCurrentUser(lecturer.Id));

        var question = await service.CreateAsync(sessionId,
            new CreateQuestionRequest(1, "Question"), CancellationToken.None);

        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal(lecturer.Id, audit.UserId);
        Assert.Equal(AuditActions.Create, audit.Action);
        Assert.Equal(AuditEntities.Question, audit.TargetEntity);
        Assert.Equal(question.Id, audit.TargetId);
    }

    [Fact]
    public async Task Import_saves_all_questions_and_audits_each_question()
    {
        await using var db = CreateDbContext();
        var (sessionId, lecturer) = await AddSessionAsync(db);
        var service = new QuestionService(db, new TestCurrentUser(lecturer.Id));

        var imported = await service.ImportAsync(sessionId,
            new ImportQuestionsRequest([
                new CreateQuestionRequest(1, "  First question  "),
                new CreateQuestionRequest(2, "Second question")
            ]), CancellationToken.None);

        Assert.Equal(2, imported.Count);
        Assert.Equal("First question", imported[0].Content);
        Assert.All(imported, question => Assert.False(question.HasRubric));
        Assert.Equal(2, db.Questions.Count());
        Assert.Equal(new[] { AuditActions.Create, AuditActions.Create }, db.AuditLogs.Select(x => x.Action).ToArray());
        Assert.Equal(imported.Select(x => x.Id).OrderBy(x => x),
            db.AuditLogs.Select(x => x.TargetId!.Value).OrderBy(x => x));
    }

    [Fact]
    public async Task Import_rejects_all_invalid_entries_without_partial_persistence()
    {
        await using var db = CreateDbContext();
        var (sessionId, lecturer) = await AddSessionAsync(db);
        var service = new QuestionService(db, new TestCurrentUser(lecturer.Id));

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.ImportAsync(sessionId,
            new ImportQuestionsRequest([
                new CreateQuestionRequest(1, " "),
                new CreateQuestionRequest(1, "Duplicate order"),
                new CreateQuestionRequest(0, "Invalid order")
            ]), CancellationToken.None));

        Assert.Contains("questions[0].content", exception.Details!.Keys);
        Assert.Contains("questions[0].orderNo", exception.Details.Keys);
        Assert.Contains("questions[1].orderNo", exception.Details.Keys);
        Assert.Contains("questions[2].orderNo", exception.Details.Keys);
        Assert.Empty(db.Questions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Import_rejects_a_missing_session()
    {
        await using var db = CreateDbContext();
        var service = new QuestionService(db, new TestCurrentUser(Guid.NewGuid()));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => service.ImportAsync(Guid.NewGuid(),
            new ImportQuestionsRequest([new CreateQuestionRequest(1, "Question")]), CancellationToken.None));

        Assert.Equal("exam_session_not_found", exception.Code);
        Assert.Empty(db.Questions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Import_rejects_a_lecturer_not_assigned_to_the_session_course()
    {
        await using var db = CreateDbContext();
        var (sessionId, _) = await AddSessionAsync(db);
        var service = new QuestionService(db, new TestCurrentUser(Guid.NewGuid()));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.ImportAsync(sessionId,
            new ImportQuestionsRequest([new CreateQuestionRequest(1, "Question")]), CancellationToken.None));

        Assert.Empty(db.Questions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Import_rejects_order_numbers_already_used_in_the_session()
    {
        await using var db = CreateDbContext();
        var (sessionId, lecturer) = await AddSessionAsync(db);
        db.Questions.Add(new Question
        {
            Id = Guid.NewGuid(),
            ExamSessionId = sessionId,
            OrderNo = 1,
            Content = "Existing question"
        });
        await db.SaveChangesAsync();
        var service = new QuestionService(db, new TestCurrentUser(lecturer.Id));

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.ImportAsync(sessionId,
            new ImportQuestionsRequest([new CreateQuestionRequest(1, "Duplicate")]), CancellationToken.None));

        Assert.Contains("questions[0].orderNo", exception.Details!.Keys);
        Assert.Single(db.Questions);
        Assert.Empty(db.AuditLogs);
    }

    private static AccessConfigDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AccessConfigDbContext(options);
    }

    private static async Task<(Guid SessionId, User Lecturer)> AddSessionAsync(AccessConfigDbContext db)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = UserRole.Lecturer.ToString() };
        var lecturer = new User
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            FullName = "Assigned Lecturer",
            Email = $"{Guid.NewGuid():N}@example.test",
            Username = Guid.NewGuid().ToString("N")
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "CS101", Name = "Course 101" };
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = lecturer.Id,
            Lecturer = lecturer
        });
        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            CreatedBy = lecturer.Id,
            Title = "Draft session",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1)
        };
        db.ExamSessions.Add(session);
        await db.SaveChangesAsync();
        return (session.Id, lecturer);
    }

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string role) => role == Roles.Lecturer;
    }
}
