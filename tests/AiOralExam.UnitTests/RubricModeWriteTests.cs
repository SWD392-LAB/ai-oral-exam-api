using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiOralExam.UnitTests;

public sealed class RubricModeWriteTests
{
    [Fact]
    public async Task Legacy_session_can_create_question_rubric()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.Legacy);
        var service = new RubricService(db, new TestCurrentUser(seed.LecturerId));

        var result = await service.SaveAsync(seed.SessionId, seed.QuestionId, ValidRequest(), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, Assert.Single(db.Rubrics).Id);
        Assert.Equal(5m, Assert.Single(result.Criteria).MaxPoints);
        Assert.Single(db.RubricCriteria);
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("Create", audit.Action);
        Assert.Equal(result.Id, audit.TargetId);
    }

    [Fact]
    public async Task Legacy_session_can_update_question_rubric()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.Legacy, withRubric: true);
        var service = new RubricService(db, new TestCurrentUser(seed.LecturerId));

        var result = await service.SaveAsync(seed.SessionId, seed.QuestionId,
            new SaveRubricRequest([new(seed.CriterionId, 1, "Updated accuracy", 6m)]), CancellationToken.None);

        Assert.Equal(seed.RubricId, result.Id);
        Assert.Equal(seed.CriterionId, Assert.Single(result.Criteria).Id);
        Assert.Equal("Updated accuracy", result.Criteria[0].Description);
        Assert.Equal(6m, result.Criteria[0].MaxPoints);
        Assert.Equal("Update", Assert.Single(db.AuditLogs).Action);
    }

    [Fact]
    public async Task Shared_version_session_rejects_rubric_creation_without_staging_entities_or_audit()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion);
        var service = new RubricService(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.SaveAsync(
            seed.SessionId, seed.QuestionId, ValidRequest(), CancellationToken.None));

        Assert.Equal("rubric_mode_conflict", exception.Code);
        Assert.Empty(db.Rubrics);
        Assert.Empty(await db.RubricCriteria.Where(x => x.RubricId != null).ToListAsync());
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_session_rejects_rubric_update_without_mutating_existing_data()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion, withRubric: true);
        var service = new RubricService(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.SaveAsync(
            seed.SessionId, seed.QuestionId,
            new SaveRubricRequest([new(seed.CriterionId, 1, "Changed", 9m)]), CancellationToken.None));

        Assert.Equal("rubric_mode_conflict", exception.Code);
        var unchanged = await db.RubricCriteria.SingleAsync(x => x.RubricId == seed.RubricId);
        Assert.Equal("Accuracy", unchanged.Description);
        Assert.Equal(5m, unchanged.MaxPoints);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Authorization_and_draft_checks_precede_the_rubric_mode_conflict()
    {
        await using (var unauthorizedDb = CreateDatabase())
        {
            var seed = await SeedAsync(unauthorizedDb, RubricMode.SharedVersion, assigned: false);
            var service = new RubricService(unauthorizedDb, new TestCurrentUser(seed.LecturerId));

            await Assert.ThrowsAsync<ForbiddenException>(() => service.SaveAsync(
                seed.SessionId, seed.QuestionId, ValidRequest(), CancellationToken.None));
            Assert.Empty(unauthorizedDb.Rubrics);
            Assert.Empty(unauthorizedDb.AuditLogs);
        }

        await using (var publishedDb = CreateDatabase())
        {
            var seed = await SeedAsync(publishedDb, RubricMode.SharedVersion, status: ExamSessionStatus.Published);
            var service = new RubricService(publishedDb, new TestCurrentUser(seed.LecturerId));

            var exception = await Assert.ThrowsAsync<ConflictException>(() => service.SaveAsync(
                seed.SessionId, seed.QuestionId, ValidRequest(), CancellationToken.None));

            Assert.Equal("exam_session_locked", exception.Code);
            Assert.Empty(publishedDb.Rubrics);
            Assert.Empty(publishedDb.AuditLogs);
        }
    }

    private static SaveRubricRequest ValidRequest() =>
        new([new(null, 1, "Accuracy", 5m)]);

    private static AccessConfigDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<Seed> SeedAsync(
        AccessConfigDbContext db,
        RubricMode mode,
        bool withRubric = false,
        bool assigned = true,
        ExamSessionStatus status = ExamSessionStatus.Draft)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var lecturer = new User
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            FullName = "Test Lecturer",
            Email = $"{Guid.NewGuid():N}@example.test",
            Username = Guid.NewGuid().ToString("N"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "RMW101", Name = "Rubric Mode Course" };
        if (assigned)
            course.Lecturers.Add(new CourseLecturer
            {
                CourseId = course.Id,
                Course = course,
                LecturerId = lecturer.Id,
                Lecturer = lecturer,
                AssignedAt = DateTime.UtcNow
            });

        RubricTemplate? template = null;
        RubricVersion? version = null;
        Guid? versionId = null;
        if (mode == RubricMode.SharedVersion)
        {
            template = new RubricTemplate
            {
                Id = Guid.NewGuid(),
                CourseId = course.Id,
                Course = course,
                Name = "Shared rubric",
                CreatedBy = lecturer.Id,
                CreatedAt = DateTime.UtcNow
            };
            version = new RubricVersion
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
            versionId = version.Id;
        }

        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            RubricMode = mode,
            RubricVersionId = versionId,
            RubricVersion = version,
            CreatedBy = lecturer.Id,
            Title = "Test Session",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1),
            TimeLimitPerAnswerSeconds = 60,
            Status = status,
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

        Guid rubricId = Guid.Empty;
        Guid criterionId = Guid.Empty;
        if (withRubric)
        {
            rubricId = Guid.NewGuid();
            criterionId = Guid.NewGuid();
            var rubric = new Rubric { Id = rubricId, QuestionId = question.Id, MaxScore = 5m };
            rubric.Criteria.Add(new RubricCriterion
            {
                Id = criterionId,
                RubricId = rubric.Id,
                Rubric = rubric,
                OrderNo = 1,
                Description = "Accuracy",
                MaxPoints = 5m
            });
            question.Rubric = rubric;
        }

        db.Roles.Add(role);
        db.Users.Add(lecturer);
        db.Courses.Add(course);
        if (template is not null)
            db.RubricTemplates.Add(template);
        if (version is not null)
            db.RubricVersions.Add(version);
        db.ExamSessions.Add(session);
        await db.SaveChangesAsync();

        return new Seed(lecturer.Id, session.Id, question.Id, rubricId, criterionId);
    }

    private sealed record Seed(Guid LecturerId, Guid SessionId, Guid QuestionId, Guid RubricId, Guid CriterionId);

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string requestedRole) => requestedRole == Roles.Lecturer;
    }
}
