using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiOralExam.UnitTests;

public sealed class ExamSessionCreationTests
{
    [Fact]
    public async Task Create_without_rubric_mode_defaults_to_shared_version_and_pins_active_platform_rubric()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var created = await service.CreateAsync(Request(seed.CourseId), CancellationToken.None);

        Assert.Equal("SharedVersion", created.RubricMode);
        Assert.Equal(seed.PlatformVersionId, created.RubricVersionId);
        var session = await db.ExamSessions.SingleAsync();
        Assert.Equal(RubricMode.SharedVersion, session.RubricMode);
        Assert.Equal(seed.PlatformVersionId, session.RubricVersionId);
        Assert.Single(db.AuditLogs);
    }

    [Fact]
    public async Task Create_with_explicit_legacy_mode_succeeds()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var created = await service.CreateAsync(Request(seed.CourseId, RubricMode.Legacy), CancellationToken.None);

        Assert.Equal("Legacy", created.RubricMode);
        Assert.Null(created.RubricVersionId);
    }

    [Fact]
    public async Task Legacy_mode_rejects_a_rubric_version_id_without_persisting()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(
            Request(seed.CourseId, RubricMode.Legacy, seed.CourseVersionId), CancellationToken.None));

        Assert.Contains("rubricVersionId", exception.Details!.Keys);
        Assert.Empty(db.ExamSessions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_mode_rejects_a_sealed_but_inactive_platform_version()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var version = await db.RubricVersions.SingleAsync(x => x.Id == seed.PlatformVersionId);
        version.IsActive = false;
        await db.SaveChangesAsync();
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(
            Request(seed.CourseId, RubricMode.SharedVersion, seed.PlatformVersionId), CancellationToken.None));

        Assert.Equal("rubric_version_not_active", exception.Code);
        Assert.Empty(db.ExamSessions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_mode_without_a_version_pins_the_active_platform_version()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var created = await service.CreateAsync(
            Request(seed.CourseId, RubricMode.SharedVersion), CancellationToken.None);

        Assert.Equal("SharedVersion", created.RubricMode);
        Assert.Equal(seed.PlatformVersionId, created.RubricVersionId);
        Assert.Single(db.ExamSessions);
        Assert.Single(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_mode_persists_selection_and_returns_it_in_create_and_detail_responses()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var created = await service.CreateAsync(
            Request(seed.CourseId, RubricMode.SharedVersion, seed.PlatformVersionId), CancellationToken.None);

        Assert.Equal("SharedVersion", created.RubricMode);
        Assert.Equal(seed.PlatformVersionId, created.RubricVersionId);
        var session = await db.ExamSessions.SingleAsync();
        Assert.Equal(RubricMode.SharedVersion, session.RubricMode);
        Assert.Equal(seed.PlatformVersionId, session.RubricVersionId);

        var detail = await new ExamSessionQueries(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator),
            new FakeInterviewResultsApi()).GetSessionAsync(created.Id);
        Assert.Equal("SharedVersion", detail.RubricMode);
        Assert.Equal(seed.PlatformVersionId, detail.RubricVersionId);
        Assert.Single(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_mode_rejects_an_unsealed_version()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(
            Request(seed.CourseId, RubricMode.SharedVersion, seed.PlatformUnsealedVersionId), CancellationToken.None));

        Assert.Equal("rubric_version_not_sealed", exception.Code);
        Assert.Empty(db.ExamSessions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_mode_rejects_a_version_with_inconsistent_criteria_score()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var version = await db.RubricVersions.SingleAsync(x => x.Id == seed.PlatformVersionId);
        version.MaxScore = 4m;
        await db.SaveChangesAsync();
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(
            Request(seed.CourseId, RubricMode.SharedVersion, seed.PlatformVersionId), CancellationToken.None));

        Assert.Equal("rubric_version_invalid", exception.Code);
        Assert.Empty(db.ExamSessions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_mode_rejects_course_scoped_versions()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => service.CreateAsync(
            Request(seed.CourseId, RubricMode.SharedVersion, seed.CourseVersionId), CancellationToken.None));

        Assert.Equal("rubric_version_not_found", exception.Code);
        Assert.Empty(db.ExamSessions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_mode_rejects_a_missing_version()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => service.CreateAsync(
            Request(seed.CourseId, RubricMode.SharedVersion, Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("rubric_version_not_found", exception.Code);
        Assert.Empty(db.ExamSessions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Unassigned_lecturer_cannot_select_a_version_while_creating_for_another_course()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, assignLecturerToCourse: false);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(
            Request(seed.CourseId, RubricMode.SharedVersion, seed.OtherCourseVersionId), CancellationToken.None));

        Assert.Empty(db.ExamSessions);
        Assert.Empty(db.AuditLogs);
    }

    private static CreateExamSessionRequest Request(
        Guid courseId, RubricMode? mode = null, Guid? versionId = null) => new(
        courseId,
        "Session",
        DateTime.UtcNow.AddHours(1),
        DateTime.UtcNow.AddHours(2),
        120,
        2,
        mode,
        versionId);

    private static AccessConfigDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<Seed> SeedAsync(AccessConfigDbContext db, bool assignLecturerToCourse = true)
    {
        var lecturerRole = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var lecturer = NewUser(Guid.NewGuid(), lecturerRole);
        var course = NewCourse("SEL101");
        var otherCourse = NewCourse("SEL102");
        if (assignLecturerToCourse)
            course.Lecturers.Add(new CourseLecturer
            {
                CourseId = course.Id,
                Course = course,
                LecturerId = lecturer.Id,
                Lecturer = lecturer,
                AssignedAt = DateTime.UtcNow
            });

        var courseVersion = NewVersion(course, lecturer.Id, sealedVersion: true);
        var otherCourseVersion = NewVersion(otherCourse, lecturer.Id, sealedVersion: true);
        var unsealedVersion = NewVersion(course, lecturer.Id, sealedVersion: false);
        var platformVersion = NewPlatformVersion(lecturer.Id, sealedVersion: true);
        var platformUnsealedVersion = NewPlatformVersion(lecturer.Id, sealedVersion: false);

        db.Roles.Add(lecturerRole);
        db.Users.Add(lecturer);
        db.Courses.AddRange(course, otherCourse);
        db.RubricTemplates.AddRange(courseVersion.Template, otherCourseVersion.Template, unsealedVersion.Template,
            platformVersion.Template, platformUnsealedVersion.Template);
        db.RubricVersions.AddRange(courseVersion, otherCourseVersion, unsealedVersion, platformVersion, platformUnsealedVersion);
        await db.SaveChangesAsync();

        return new Seed(lecturer.Id, course.Id, courseVersion.Id, otherCourseVersion.Id,
            unsealedVersion.Id, platformVersion.Id, platformUnsealedVersion.Id);
    }

    private static Course NewCourse(string code) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        Name = $"Course {code}"
    };

    private static RubricVersion NewVersion(Course course, Guid creatorId, bool sealedVersion)
    {
        var template = new RubricTemplate
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            Name = "Selection rubric",
            CreatedBy = creatorId,
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
            IsSealed = sealedVersion,
            CreatedBy = creatorId,
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
        return version;
    }

    private static RubricVersion NewPlatformVersion(Guid creatorId, bool sealedVersion)
    {
        var template = new RubricTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Platform selection rubric",
            CreatedBy = creatorId,
            CreatedAt = DateTime.UtcNow
        };
        var version = new RubricVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            Template = template,
            VersionNo = 1,
            MaxScore = 5m,
            IsSealed = sealedVersion,
            IsActive = sealedVersion,
            CreatedBy = creatorId,
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
        return version;
    }

    private static User NewUser(Guid id, Role role) => new()
    {
        Id = id,
        RoleId = role.Id,
        Role = role,
        FullName = "Test Lecturer",
        Email = $"{id:N}@example.test",
        Username = id.ToString("N"),
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };

    private sealed record Seed(
        Guid LecturerId,
        Guid CourseId,
        Guid CourseVersionId,
        Guid OtherCourseVersionId,
        Guid UnsealedVersionId,
        Guid PlatformVersionId,
        Guid PlatformUnsealedVersionId);

    private sealed class TestCurrentUser(Guid id, string role = Roles.Lecturer) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => role;
        public bool IsInRole(string requestedRole) => requestedRole == role;
    }

    private sealed class FakeInterviewResultsApi : IInterviewResultsApi
    {
        public Task<AttemptResult?> GetAttemptResultAsync(Guid attemptId, CancellationToken ct = default) =>
            Task.FromResult<AttemptResult?>(null);

        public Task<IReadOnlyList<AttemptCounts>> GetAttemptCountsAsync(
            IReadOnlyCollection<Guid> sessionIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AttemptCounts>>([]);

        public Task<IReadOnlyList<AttemptResult>> GetSessionResultsAsync(
            Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AttemptResult>>([]);
    }
}
