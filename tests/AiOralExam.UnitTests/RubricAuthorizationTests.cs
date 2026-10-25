using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.UnitTests;

public sealed class RubricAuthorizationTests
{
    [Fact]
    public async Task Assigned_lecturer_can_read_rubric()
    {
        await using var db = CreateDatabase();
        var (sessionId, questionId, lecturerId, rubricId) = await SeedScenarioAsync(db, ExamSessionStatus.Draft);
        var service = new RubricService(db, new TestCurrentUser(lecturerId, Roles.Lecturer));

        var rubric = await service.GetAsync(sessionId, questionId, CancellationToken.None);

        Assert.Equal(rubricId, rubric.Id);
        Assert.Single(rubric.Criteria);
    }

    [Fact]
    public async Task Unassigned_lecturer_cannot_read_rubric()
    {
        await using var db = CreateDatabase();
        var (sessionId, questionId, _, _) = await SeedScenarioAsync(db, ExamSessionStatus.Draft);
        var service = new RubricService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Lecturer));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetAsync(sessionId, questionId, CancellationToken.None));
    }

    [Fact]
    public async Task Administrator_can_read_rubric()
    {
        await using var db = CreateDatabase();
        var (sessionId, questionId, _, rubricId) = await SeedScenarioAsync(db, ExamSessionStatus.Draft);
        var service = new RubricService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Administrator));

        var rubric = await service.GetAsync(sessionId, questionId, CancellationToken.None);

        Assert.Equal(rubricId, rubric.Id);
        Assert.Single(rubric.Criteria);
    }

    [Theory]
    [InlineData(ExamSessionStatus.Published)]
    [InlineData(ExamSessionStatus.Closed)]
    public async Task Assigned_lecturer_can_read_rubric_for_non_draft_session(ExamSessionStatus status)
    {
        await using var db = CreateDatabase();
        var (sessionId, questionId, lecturerId, rubricId) = await SeedScenarioAsync(db, status);
        var service = new RubricService(db, new TestCurrentUser(lecturerId, Roles.Lecturer));

        var rubric = await service.GetAsync(sessionId, questionId, CancellationToken.None);

        Assert.Equal(rubricId, rubric.Id);
        Assert.Single(rubric.Criteria);
    }

    private static AccessConfigDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(Guid SessionId, Guid QuestionId, Guid LecturerId, Guid RubricId)> SeedScenarioAsync(
        AccessConfigDbContext db,
        ExamSessionStatus status)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var lecturer = new User
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            FullName = "Assigned Lecturer",
            Email = "lecturer@example.test",
            Username = "assigned.lecturer",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "TST101", Name = "Test Course" };
        var assignment = new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = lecturer.Id,
            Lecturer = lecturer,
            AssignedAt = DateTime.UtcNow,
        };
        course.Lecturers.Add(assignment);
        lecturer.CourseAssignments.Add(assignment);

        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            CreatedBy = lecturer.Id,
            Title = "Test Session",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1),
            TimeLimitPerAnswerSeconds = 60,
            Status = status,
            CreatedAt = DateTime.UtcNow,
        };
        var question = new Question
        {
            Id = Guid.NewGuid(),
            ExamSessionId = session.Id,
            OrderNo = 1,
            Content = "Test question",
        };
        var rubric = new Rubric
        {
            Id = Guid.NewGuid(),
            QuestionId = question.Id,
            MaxScore = 10m,
        };
        var criterion = new RubricCriterion
        {
            Id = Guid.NewGuid(),
            RubricId = rubric.Id,
            Rubric = rubric,
            OrderNo = 1,
            Description = "Accuracy",
            MaxPoints = 10m,
        };
        rubric.Criteria.Add(criterion);
        question.Rubric = rubric;
        session.Questions.Add(question);
        course.ExamSessions.Add(session);

        db.Roles.Add(role);
        db.Users.Add(lecturer);
        db.Courses.Add(course);
        db.ExamSessions.Add(session);
        db.Questions.Add(question);
        db.Rubrics.Add(rubric);
        db.RubricCriteria.Add(criterion);
        await db.SaveChangesAsync();

        return (session.Id, question.Id, lecturer.Id, rubric.Id);
    }

    private sealed class TestCurrentUser(Guid id, string role) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => role;
        public bool IsInRole(string requestedRole) => role == requestedRole;
    }
}
