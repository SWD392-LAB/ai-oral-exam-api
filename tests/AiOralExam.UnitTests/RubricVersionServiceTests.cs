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
        Assert.False(created.IsActive);
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
    public async Task Lecturer_can_read_global_versions_without_course_assignment_but_cannot_create()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var adminService = new RubricVersionService(db,
            new TestCurrentUser(seed.LecturerId, Roles.Administrator));
        var version = await adminService.CreateAsync(seed.TemplateId, ValidRequest(), CancellationToken.None);
        var service = new RubricVersionService(db, new TestCurrentUser(Guid.NewGuid()));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(seed.TemplateId,
            ValidRequest(), CancellationToken.None));
        Assert.Single(await service.ListAsync(seed.TemplateId, CancellationToken.None));
        Assert.Equal(version.Id,
            (await service.GetAsync(seed.TemplateId, version.Id, CancellationToken.None)).Id);
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
    public async Task Administrator_can_create_a_platform_version_independent_of_course_assignment()
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
    public async Task Administrator_cannot_extend_a_historical_course_scoped_template()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var legacyTemplate = new RubricTemplate
        {
            Id = Guid.NewGuid(), CourseId = seed.CourseId, Course = seed.Course,
            Name = "Historical course template", CreatedBy = seed.LecturerId, CreatedAt = DateTime.UtcNow
        };
        db.RubricTemplates.Add(legacyTemplate);
        await db.SaveChangesAsync();
        var service = new RubricVersionService(db,
            new TestCurrentUser(seed.LecturerId, Roles.Administrator));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(
            legacyTemplate.Id, ValidRequest(), CancellationToken.None));

        Assert.Equal("rubric_template_not_platform_owned", exception.Code);
        Assert.Empty(db.RubricVersions);
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

    [Fact]
    public async Task Platform_version_activation_is_admin_only_and_switches_the_active_immutable_version()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var template = await db.RubricTemplates.SingleAsync(x => x.Id == seed.TemplateId);
        template.CourseId = null;
        template.Course = null;
        await db.SaveChangesAsync();

        var admin = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));
        var first = await admin.CreateAsync(seed.TemplateId, ValidRequest(), CancellationToken.None);
        var activatedFirst = await admin.ActivateAsync(seed.TemplateId, first.Id, CancellationToken.None);
        Assert.True(activatedFirst.IsActive);

        var second = await admin.CreateAsync(seed.TemplateId,
            new CreateRubricVersionRequest([new(1, "Updated criterion", 7m)]), CancellationToken.None);
        var activatedSecond = await admin.ActivateAsync(seed.TemplateId, second.Id, CancellationToken.None);

        Assert.True(activatedSecond.IsActive);
        Assert.False((await admin.GetAsync(seed.TemplateId, first.Id, CancellationToken.None)).IsActive);
        Assert.Equal(first.Criteria, (await admin.GetAsync(seed.TemplateId, first.Id, CancellationToken.None)).Criteria);

        var lecturer = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Lecturer));
        Assert.Equal(second.Id, (await lecturer.GetAsync(seed.TemplateId, second.Id, CancellationToken.None)).Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => lecturer.ActivateAsync(
            seed.TemplateId, first.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Platform_version_activation_switches_the_global_active_version_across_templates()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var secondTemplate = new RubricTemplate
        {
            Id = Guid.NewGuid(), Name = "Second platform template", CreatedBy = seed.LecturerId,
            CreatedAt = DateTime.UtcNow
        };
        db.RubricTemplates.Add(secondTemplate);
        await db.SaveChangesAsync();
        var admin = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Administrator));
        var first = await admin.CreateAsync(seed.TemplateId, ValidRequest(), CancellationToken.None);
        var second = await admin.CreateAsync(secondTemplate.Id, ValidRequest(), CancellationToken.None);

        await admin.ActivateAsync(seed.TemplateId, first.Id, CancellationToken.None);
        await admin.ActivateAsync(secondTemplate.Id, second.Id, CancellationToken.None);

        Assert.False((await admin.GetAsync(seed.TemplateId, first.Id, CancellationToken.None)).IsActive);
        Assert.True((await admin.GetAsync(secondTemplate.Id, second.Id, CancellationToken.None)).IsActive);
        Assert.Equal(new[] { second.Id }, await db.RubricVersions.AsNoTracking()
            .Where(x => x.IsActive).Select(x => x.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData(Roles.Lecturer)]
    [InlineData(Roles.Administrator)]
    public async Task Lecturer_or_administrator_can_read_active_platform_version_with_ordered_criteria(string role)
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var version = NewVersion(seed.TemplateId, null, 1, isSealed: true, isActive: true, seed.LecturerId);
        var criteria = new[]
        {
            new RubricCriterion { Id = Guid.NewGuid(), RubricVersionId = version.Id, RubricVersion = version,
                OrderNo = 2, Description = "Clarity", MaxPoints = 5m },
            new RubricCriterion { Id = Guid.NewGuid(), RubricVersionId = version.Id, RubricVersion = version,
                OrderNo = 1, Description = "Accuracy", MaxPoints = 5m }
        };
        db.RubricVersions.Add(version);
        db.RubricCriteria.AddRange(criteria);
        await db.SaveChangesAsync();
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, role));

        var result = await service.GetActivePlatformAsync(CancellationToken.None);

        Assert.Equal(version.Id, result.Id);
        Assert.Equal(new[] { 1, 2 }, result.Criteria.Select(x => x.OrderNo));
        Assert.Equal(new[] { "Accuracy", "Clarity" }, result.Criteria.Select(x => x.Description));
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Active_platform_lookup_excludes_inactive_unsealed_and_course_scoped_versions()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var active = NewVersion(seed.TemplateId, null, 1, isSealed: true, isActive: true, seed.LecturerId);
        var inactive = NewVersion(seed.TemplateId, null, 2, isSealed: true, isActive: false, seed.LecturerId);
        var unsealed = NewVersion(seed.TemplateId, null, 3, isSealed: false, isActive: false, seed.LecturerId);
        var courseTemplate = new RubricTemplate
        {
            Id = Guid.NewGuid(), CourseId = seed.CourseId, Course = seed.Course,
            Name = "Course rubric", CreatedBy = seed.LecturerId, CreatedAt = DateTime.UtcNow
        };
        var courseScoped = NewVersion(courseTemplate.Id, seed.CourseId, 1,
            isSealed: true, isActive: false, seed.LecturerId);
        courseScoped.Template = courseTemplate;
        db.RubricTemplates.Add(courseTemplate);
        db.RubricVersions.AddRange(active, inactive, unsealed, courseScoped);
        await db.SaveChangesAsync();
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Lecturer));

        var result = await service.GetActivePlatformAsync(CancellationToken.None);

        Assert.Equal(active.Id, result.Id);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unsealed")]
    [InlineData("course-scoped")]
    public async Task Active_platform_lookup_ignores_each_nonqualifying_version_state(string scenario)
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var templateId = seed.TemplateId;
        Guid? courseId = null;
        var isSealed = true;
        var isActive = true;
        if (scenario == "inactive")
            isActive = false;
        else if (scenario == "unsealed")
            isSealed = false;
        else
        {
            var courseTemplate = new RubricTemplate
            {
                Id = Guid.NewGuid(), CourseId = seed.CourseId, Course = seed.Course,
                Name = "Course rubric", CreatedBy = seed.LecturerId, CreatedAt = DateTime.UtcNow
            };
            db.RubricTemplates.Add(courseTemplate);
            templateId = courseTemplate.Id;
            courseId = seed.CourseId;
        }

        db.RubricVersions.Add(NewVersion(templateId, courseId, 1, isSealed, isActive, seed.LecturerId));
        await db.SaveChangesAsync();
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId, Roles.Lecturer));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetActivePlatformAsync(CancellationToken.None));

        Assert.Equal("active_platform_rubric_not_found", exception.Code);
    }

    [Fact]
    public async Task Active_platform_lookup_returns_not_found_when_no_qualifying_version_exists()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetActivePlatformAsync(CancellationToken.None));

        Assert.Equal("active_platform_rubric_not_found", exception.Code);
    }

    [Fact]
    public async Task Active_platform_lookup_does_not_choose_when_multiple_qualifying_versions_exist()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        // The InMemory provider does not enforce the production unique partial index; this deliberately simulates corrupted data.
        db.RubricVersions.AddRange(
            NewVersion(seed.TemplateId, null, 1, isSealed: true, isActive: true, seed.LecturerId),
            NewVersion(seed.TemplateId, null, 2, isSealed: true, isActive: true, seed.LecturerId));
        await db.SaveChangesAsync();
        var service = new RubricVersionService(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetActivePlatformAsync(CancellationToken.None));

        Assert.Equal("multiple_active_platform_rubrics", exception.Code);
    }

    [Theory]
    [InlineData(Roles.Student, true)]
    [InlineData(Roles.Lecturer, false)]
    public async Task Active_platform_lookup_requires_an_authenticated_lecturer_or_administrator(
        string role, bool isAuthenticated)
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db);
        var service = new RubricVersionService(db,
            new TestCurrentUser(seed.LecturerId, role, isAuthenticated));

        if (isAuthenticated)
            await Assert.ThrowsAsync<ForbiddenException>(() => service.GetActivePlatformAsync(CancellationToken.None));
        else
            await Assert.ThrowsAsync<UnauthorizedException>(() => service.GetActivePlatformAsync(CancellationToken.None));
    }

    private static RubricVersion NewVersion(
        Guid templateId, Guid? courseId, int versionNo, bool isSealed, bool isActive, Guid createdBy) => new()
    {
        Id = Guid.NewGuid(), TemplateId = templateId, CourseId = courseId, VersionNo = versionNo,
        MaxScore = 10m, IsSealed = isSealed, IsActive = isActive, CreatedBy = createdBy,
        CreatedAt = DateTime.UtcNow
    };

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
            Id = Guid.NewGuid(), Name = "Template",
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
