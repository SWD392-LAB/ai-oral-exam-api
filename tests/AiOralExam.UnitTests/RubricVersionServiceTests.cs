using AiOralExam.Modules.AccessConfig.Application.RubricVersions;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiOralExam.UnitTests;

public sealed class RubricVersionServiceTests
{
    [Fact]
    public async Task Create_seals_version_with_ordered_criteria_and_stable_ids()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));
        var request = new CreateRubricVersionRequest(
        [
            new(2, "  Clarity  ", 5m),
            new(1, "Accuracy", 5m)
        ]);

        var created = await service.CreateAsync(seed.TemplateId, request, CancellationToken.None);

        Assert.Equal(1, created.VersionNo);
        Assert.True(created.IsSealed);
        Assert.Equal(10m, created.MaxScore);
        Assert.Equal(new[] { 1, 2 }, created.Criteria.Select(x => x.OrderNo));
        Assert.All(created.Criteria, x => Assert.NotEqual(Guid.Empty, x.Id));
        Assert.Equal("Clarity", created.Criteria[1].Description);
        Assert.True((await db.RubricVersions.SingleAsync()).IsSealed);
        Assert.All(await db.RubricCriteria.ToListAsync(), x =>
        {
            Assert.Equal(created.Id, x.RubricVersionId);
            Assert.Null(x.RubricId);
        });
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(seed.LecturerId, audit.UserId);
        Assert.Equal("Create", audit.Action);
        Assert.Equal("RubricVersion", audit.TargetEntity);
        Assert.Equal(created.Id, audit.TargetId);
    }

    [Fact]
    public async Task Create_allocates_sequential_numbers_without_replacing_prior_version_criteria()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));
        var request = ValidRequest();

        var first = await service.CreateAsync(seed.TemplateId, request, CancellationToken.None);
        var firstCriterionIds = first.Criteria.Select(x => x.Id).ToArray();
        var second = await service.CreateAsync(seed.TemplateId, request, CancellationToken.None);

        Assert.Equal(1, first.VersionNo);
        Assert.Equal(2, second.VersionNo);
        Assert.Equal(firstCriterionIds, (await service.GetAsync(seed.TemplateId, first.Id, CancellationToken.None))
            .Criteria.Select(x => x.Id));
        Assert.Equal(2, (await service.ListAsync(seed.TemplateId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Create_rejects_empty_criteria()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));

        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(seed.TemplateId,
            new CreateRubricVersionRequest([]), CancellationToken.None));
        Assert.Empty(db.RubricVersions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000)]
    [InlineData(1.001)]
    public async Task Create_rejects_invalid_criterion_scores(decimal maxPoints)
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));

        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(seed.TemplateId,
            new CreateRubricVersionRequest([new(1, "Criterion", maxPoints)]), CancellationToken.None));
        Assert.Empty(db.RubricVersions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_blank_descriptions(string description)
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));

        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(seed.TemplateId,
            new CreateRubricVersionRequest([new(1, description, 5m)]), CancellationToken.None));
        Assert.Empty(db.RubricVersions);
    }

    [Fact]
    public async Task Create_rejects_duplicate_or_nonpositive_order_numbers()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));

        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(seed.TemplateId,
            new CreateRubricVersionRequest([new(1, "A", 5m), new(1, "B", 5m)]), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(seed.TemplateId,
            new CreateRubricVersionRequest([new(0, "A", 5m)]), CancellationToken.None));
        Assert.Empty(db.RubricVersions);
    }

    [Fact]
    public async Task Create_rejects_total_above_schema_limit()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));

        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(seed.TemplateId,
            new CreateRubricVersionRequest([new(1, "A", 600m), new(2, "B", 400m)]), CancellationToken.None));
        Assert.Empty(db.RubricVersions);
    }

    [Fact]
    public async Task Unassigned_lecturer_cannot_create_or_read_versions()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db, new TestCurrentUser(Guid.NewGuid()));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(seed.TemplateId,
            ValidRequest(), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.ListAsync(seed.TemplateId, CancellationToken.None));
    }

    [Fact]
    public async Task Student_with_a_stale_lecturer_assignment_cannot_create_or_read_versions()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var lecturerService = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));
        var existing = await lecturerService.CreateAsync(seed.TemplateId, ValidRequest(), CancellationToken.None);
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Student));
        var auditCount = await db.AuditLogs.CountAsync();

        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(seed.TemplateId,
            ValidRequest(), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.ListAsync(seed.TemplateId, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.GetAsync(seed.TemplateId, existing.Id, CancellationToken.None));

        Assert.Single(db.RubricVersions);
        Assert.Equal(auditCount, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Unauthenticated_caller_cannot_create_or_read_versions_through_application_service()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var existing = await new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator))
            .CreateAsync(seed.TemplateId, ValidRequest(), CancellationToken.None);
        var service = new RubricVersionService(db,
            new TestCurrentUser(seed.LecturerId, Roles.Lecturer, isAuthenticated: false));

        await Assert.ThrowsAsync<UnauthorizedException>(() => service.CreateAsync(seed.TemplateId,
            ValidRequest(), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.ListAsync(seed.TemplateId, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.GetAsync(seed.TemplateId, existing.Id, CancellationToken.None));
        Assert.Single(db.RubricVersions);
    }

    [Fact]
    public async Task Administrator_can_create_a_version_for_a_course_without_an_assignment()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var adminRole = new Role { Id = Guid.NewGuid(), Name = Roles.Administrator };
        var admin = new User
        {
            Id = Guid.NewGuid(), RoleId = adminRole.Id, Role = adminRole,
            FullName = "Test Administrator", Email = "admin@example.test", Username = "admin",
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Roles.Add(adminRole);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var service = new RubricVersionService(db, new TestCurrentUser(admin.Id, Roles.Administrator));

        var created = await service.CreateAsync(seed.TemplateId, ValidRequest(), CancellationToken.None);

        Assert.Equal(1, created.VersionNo);
        Assert.True(created.IsSealed);
    }

    [Fact]
    public async Task Assigned_lecturer_can_read_but_cannot_manage_versions()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var adminService = new RubricVersionService(db,
            new TestCurrentUser(seed.LecturerId, Roles.Administrator));
        var version = await adminService.CreateAsync(seed.TemplateId, ValidRequest(), CancellationToken.None);
        var lecturerService = new RubricVersionService(db,
            new TestCurrentUser(seed.LecturerId, Roles.Lecturer));

        Assert.Single(await lecturerService.ListAsync(seed.TemplateId, CancellationToken.None));
        Assert.Equal(version.Id,
            (await lecturerService.GetAsync(seed.TemplateId, version.Id, CancellationToken.None)).Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => lecturerService.CreateAsync(
            seed.TemplateId, ValidRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task Version_must_match_template_for_detail_lookup()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var otherTemplate = new RubricTemplate
        {
            Id = Guid.NewGuid(), CourseId = seed.CourseId, Course = seed.Course,
            Name = "Other", CreatedBy = seed.LecturerId, CreatedAt = DateTime.UtcNow
        };
        db.RubricTemplates.Add(otherTemplate);
        await db.SaveChangesAsync();
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));
        var version = await service.CreateAsync(seed.TemplateId, ValidRequest(), CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(otherTemplate.Id, version.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Creating_a_new_version_does_not_change_an_existing_sealed_version()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));
        var original = await service.CreateAsync(seed.TemplateId, ValidRequest(), CancellationToken.None);
        var originalCriteria = original.Criteria.ToArray();

        await service.CreateAsync(seed.TemplateId,
            new CreateRubricVersionRequest([new(1, "New criterion", 3m)]), CancellationToken.None);

        var unchanged = await service.GetAsync(seed.TemplateId, original.Id, CancellationToken.None);
        Assert.True(unchanged.IsSealed);
        Assert.Equal(originalCriteria, unchanged.Criteria);
    }

    private static CreateRubricVersionRequest ValidRequest() =>
        new([new(1, "Accuracy", 5m), new(2, "Clarity", 5m)]);

    private static AccessConfigDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<Seed> SeedAsync(AccessConfigDbContext db)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var lecturer = new User
        {
            Id = Guid.NewGuid(), RoleId = role.Id, Role = role,
            FullName = "Test Lecturer", Email = "lecturer@example.test", Username = "lecturer",
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "TST101", Name = "Test Course" };
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id, Course = course, LecturerId = lecturer.Id, Lecturer = lecturer,
            AssignedAt = DateTime.UtcNow
        });
        var template = new RubricTemplate
        {
            Id = Guid.NewGuid(), CourseId = course.Id, Course = course, Name = "Template",
            CreatedBy = lecturer.Id, CreatedAt = DateTime.UtcNow
        };
        db.Roles.Add(role);
        db.Users.Add(lecturer);
        db.Courses.Add(course);
        db.RubricTemplates.Add(template);
        await db.SaveChangesAsync();
        return new Seed(lecturer.Id, course.Id, course, template.Id);
    }

    private sealed record Seed(Guid LecturerId, Guid CourseId, Course Course, Guid TemplateId);

    private sealed class TestCurrentUser(
        Guid id, string role = Roles.Lecturer, bool isAuthenticated = true) : ICurrentUser
    {
        public bool IsAuthenticated => isAuthenticated;
        public Guid Id => id;
        public string? Role => role;
        public bool IsInRole(string requestedRole) => requestedRole == role;
    }
}
