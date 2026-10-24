using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Participants;
using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiOralExam.UnitTests;

public class AuditLogDescriptionTests
{
    [Fact]
    public async Task Saving_rubric_uses_create_then_update_actions()
    {
        await using var db = CreateDbContext();
        var (session, lecturer) = await AddSessionAsync(db);
        var question = new Question
        {
            Id = Guid.NewGuid(),
            ExamSessionId = session.Id,
            OrderNo = 1,
            Content = "Question"
        };
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        var service = new RubricService(db, new TestCurrentUser(lecturer.Id));

        var created = await service.SaveAsync(session.Id, question.Id,
            new SaveRubricRequest([new SaveRubricCriterionRequest(null, 1, "Criterion", 5m)]),
            CancellationToken.None);
        await service.SaveAsync(session.Id, question.Id,
            new SaveRubricRequest([new SaveRubricCriterionRequest(created.Criteria.Single().Id, 1, "Updated criterion", 5m)]),
            CancellationToken.None);

        Assert.Equal(new[] { AuditActions.Create, AuditActions.Update },
            db.AuditLogs.Select(x => x.Action).ToArray());
        Assert.All(db.AuditLogs, audit =>
        {
            Assert.Equal(AuditEntities.Rubric, audit.TargetEntity);
            Assert.Contains(question.Id.ToString(), audit.Description ?? string.Empty);
        });
    }

    [Fact]
    public async Task Import_participants_uses_canonical_action_name()
    {
        await using var db = CreateDbContext();
        var (session, lecturer) = await AddSessionAsync(db);
        var studentRole = new Role { Id = Guid.NewGuid(), Name = UserRole.Student.ToString() };
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            RoleId = studentRole.Id,
            Role = studentRole,
            FullName = "Student One",
            Email = "student@example.test",
            Username = "student.one",
            StudentCode = "S001"
        });
        await db.SaveChangesAsync();
        var service = new ParticipantService(db, new TestCurrentUser(lecturer.Id));

        await service.ImportAsync(session.Id, new ImportStudentsRequest(["S001", "MISSING"]), CancellationToken.None);

        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal(AuditActions.ImportParticipants, audit.Action);
        Assert.Equal(AuditEntities.ExamSession, audit.TargetEntity);
        Assert.Equal("1 participants imported, 1 skipped", audit.Description);
    }

    private static AccessConfigDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AccessConfigDbContext(options);
    }

    private static async Task<(ExamSession Session, User Lecturer)> AddSessionAsync(AccessConfigDbContext db)
    {
        var lecturerRole = new Role { Id = Guid.NewGuid(), Name = UserRole.Lecturer.ToString() };
        var lecturer = new User
        {
            Id = Guid.NewGuid(),
            RoleId = lecturerRole.Id,
            Role = lecturerRole,
            FullName = "Lecturer One",
            Email = "lecturer@example.test",
            Username = "lecturer.one",
            LecturerCode = "L001"
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "IT101", Name = "Introduction" };
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
            Title = "Midterm",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1)
        };
        db.ExamSessions.Add(session);
        await db.SaveChangesAsync();
        return (session, lecturer);
    }

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string role) => role == Roles.Lecturer;
    }
}
