using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiOralExam.UnitTests;

public sealed class RubricValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Save_rejects_nonpositive_criterion_points(decimal maxPoints)
    {
        await using var db = CreateDatabase();
        var service = new RubricService(db, new TestCurrentUser(Guid.NewGuid()));
        var request = new SaveRubricRequest([new SaveRubricCriterionRequest(null, 1, "Accuracy", maxPoints)]);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), request, CancellationToken.None));
    }

    [Fact]
    public async Task Save_accepts_positive_points_and_preserves_legacy_criterion_identity()
    {
        await using var db = CreateDatabase();
        var (sessionId, questionId, lecturerId, rubricId, criterionId) = await SeedLegacyRubricAsync(db);
        var service = new RubricService(db, new TestCurrentUser(lecturerId));
        var request = new SaveRubricRequest(
            [new SaveRubricCriterionRequest(criterionId, 1, "Updated accuracy", 6.5m)]);

        var result = await service.SaveAsync(sessionId, questionId, request, CancellationToken.None);

        var savedCriterion = await db.RubricCriteria.SingleAsync();
        Assert.Equal(rubricId, result.Id);
        Assert.Equal(criterionId, result.Criteria.Single().Id);
        Assert.Equal("Updated accuracy", result.Criteria.Single().Description);
        Assert.Equal(6.5m, result.Criteria.Single().MaxPoints);
        Assert.Equal(rubricId, savedCriterion.RubricId);
        Assert.Null(savedCriterion.RubricVersionId);
    }

    private static AccessConfigDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<(Guid SessionId, Guid QuestionId, Guid LecturerId, Guid RubricId, Guid CriterionId)>
        SeedLegacyRubricAsync(AccessConfigDbContext db)
    {
        var lecturerId = Guid.NewGuid();
        var role = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var lecturer = new User
        {
            Id = lecturerId,
            RoleId = role.Id,
            Role = role,
            FullName = "Test Lecturer",
            Email = "lecturer@example.test",
            Username = "test.lecturer",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "TST101", Name = "Test Course" };
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = lecturer.Id,
            Lecturer = lecturer,
            AssignedAt = DateTime.UtcNow
        });
        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            RubricMode = RubricMode.Legacy,
            CreatedBy = lecturer.Id,
            Title = "Legacy Session",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1),
            TimeLimitPerAnswerSeconds = 60,
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
        var rubric = new Rubric { Id = Guid.NewGuid(), QuestionId = question.Id, MaxScore = 10m };
        var criterion = new RubricCriterion
        {
            Id = Guid.NewGuid(),
            RubricId = rubric.Id,
            Rubric = rubric,
            OrderNo = 1,
            Description = "Accuracy",
            MaxPoints = 10m
        };
        rubric.Criteria.Add(criterion);
        question.Rubric = rubric;
        session.Questions.Add(question);

        db.Roles.Add(role);
        db.Users.Add(lecturer);
        db.Courses.Add(course);
        db.ExamSessions.Add(session);
        await db.SaveChangesAsync();

        return (session.Id, question.Id, lecturer.Id, rubric.Id, criterion.Id);
    }

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string requestedRole) => requestedRole == Roles.Lecturer;
    }
}
