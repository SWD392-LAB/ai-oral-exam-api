using AiOralExam.Modules.AccessConfig.Application.RubricTemplates;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiOralExam.UnitTests;

public sealed class RubricTemplateServiceTests
{
    [Fact]
    public async Task Administrator_can_create_template_with_creator_and_timestamp()
    {
        await using var db = CreateDatabase();
        var (user, course) = await SeedAssignedCourseAsync(db);
        var service = new RubricTemplateService(db, new TestCurrentUser(user.Id, Roles.Administrator));

        var created = await service.CreateAsync(new CreateRubricTemplateRequest("  Oral rubric  ", course.Id),
            CancellationToken.None);

        Assert.Equal(course.Id, created.CourseId);
        Assert.Equal("Oral rubric", created.Name);
        Assert.Equal(user.Id, created.CreatedBy);
        Assert.NotEqual(default, created.CreatedAt);
        Assert.Equal(course.Code, created.CourseCode);
        Assert.Equal("RubricTemplate", Assert.Single(db.AuditLogs).TargetEntity);
        Assert.Single(db.RubricTemplates);
    }

    [Fact]
    public async Task Assigned_lecturer_is_read_only_for_rubric_templates()
    {
        await using var db = CreateDatabase();
        var (user, course) = await SeedAssignedCourseAsync(db);
        var template = NewTemplate(user.Id, course, "Existing");
        db.RubricTemplates.Add(template);
        await db.SaveChangesAsync();
        var service = new RubricTemplateService(db, new TestCurrentUser(user.Id));

        Assert.Single(await service.ListAsync(course.Id, CancellationToken.None));
        Assert.Equal(template.Id, (await service.GetAsync(template.Id, CancellationToken.None)).Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(
            new CreateRubricTemplateRequest("No write", course.Id), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.UpdateAsync(template.Id,
            new UpdateRubricTemplateRequest("Changed"), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.DeleteAsync(template.Id, CancellationToken.None));
        Assert.Equal("Existing", template.Name);
    }

    [Fact]
    public async Task Lecturer_cannot_create_or_list_templates_for_unassigned_course()
    {
        await using var db = CreateDatabase();
        var (user, _) = await SeedAssignedCourseAsync(db, "OWNED");
        var otherCourse = new Course { Id = Guid.NewGuid(), Code = "OTHER", Name = "Other Course" };
        db.Courses.Add(otherCourse);
        await db.SaveChangesAsync();
        var service = new RubricTemplateService(db, new TestCurrentUser(user.Id));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(
            new CreateRubricTemplateRequest("Not allowed", otherCourse.Id), CancellationToken.None));
        Assert.Empty(await service.ListAsync(otherCourse.Id, CancellationToken.None));
        Assert.Empty(db.RubricTemplates);
    }

    [Fact]
    public async Task Student_with_a_stale_lecturer_assignment_cannot_manage_templates()
    {
        await using var db = CreateDatabase();
        var (user, course) = await SeedAssignedCourseAsync(db);
        var template = NewTemplate(user.Id, course, "Existing");
        db.RubricTemplates.Add(template);
        await db.SaveChangesAsync();
        var service = new RubricTemplateService(db, new TestCurrentUser(user.Id, Roles.Student));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(
            new CreateRubricTemplateRequest("No access", course.Id), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.ListAsync(null, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.GetAsync(template.Id, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.UpdateAsync(template.Id,
            new UpdateRubricTemplateRequest("Changed"), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.DeleteAsync(template.Id, CancellationToken.None));

        Assert.Equal("Existing", (await db.RubricTemplates.SingleAsync()).Name);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Administrator_can_manage_templates_in_courses_without_an_assignment()
    {
        await using var db = CreateDatabase();
        await SeedAssignedCourseAsync(db, "OWNED");
        var otherCourse = new Course { Id = Guid.NewGuid(), Code = "OTHER", Name = "Other Course" };
        db.Courses.Add(otherCourse);
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
        var service = new RubricTemplateService(db, new TestCurrentUser(admin.Id, Roles.Administrator));

        var created = await service.CreateAsync(
            new CreateRubricTemplateRequest("Admin template", otherCourse.Id), CancellationToken.None);
        var listed = await service.ListAsync(otherCourse.Id, CancellationToken.None);

        Assert.Equal(otherCourse.Id, created.CourseId);
        Assert.Equal(admin.Id, created.CreatedBy);
        Assert.Equal(created.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task Unauthenticated_caller_cannot_manage_templates_through_application_service()
    {
        await using var db = CreateDatabase();
        var (user, course) = await SeedAssignedCourseAsync(db);
        var template = NewTemplate(user.Id, course, "Existing");
        db.RubricTemplates.Add(template);
        await db.SaveChangesAsync();
        var service = new RubricTemplateService(db, new TestCurrentUser(user.Id, Roles.Lecturer, false));

        await Assert.ThrowsAsync<UnauthorizedException>(() => service.CreateAsync(
            new CreateRubricTemplateRequest("No access", course.Id), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.ListAsync(null, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.GetAsync(template.Id, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.UpdateAsync(template.Id,
            new UpdateRubricTemplateRequest("Changed"), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.DeleteAsync(template.Id, CancellationToken.None));

        Assert.Equal("Existing", (await db.RubricTemplates.SingleAsync()).Name);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task List_and_get_return_only_accessible_template_metadata()
    {
        await using var db = CreateDatabase();
        var (user, course) = await SeedAssignedCourseAsync(db);
        var otherCourse = new Course { Id = Guid.NewGuid(), Code = "OTHER", Name = "Other Course" };
        db.Courses.Add(otherCourse);
        var first = NewTemplate(user.Id, course, "First");
        var second = NewTemplate(user.Id, course, "Second");
        var outside = NewTemplate(user.Id, otherCourse, "Outside");
        db.RubricTemplates.AddRange(first, second, outside);
        await db.SaveChangesAsync();
        var service = new RubricTemplateService(db, new TestCurrentUser(user.Id));

        var list = await service.ListAsync(null, CancellationToken.None);
        var filtered = await service.ListAsync(course.Id, CancellationToken.None);
        var detail = await service.GetAsync(first.Id, CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Equal(2, filtered.Count);
        Assert.All(list, x => Assert.Equal(course.Id, x.CourseId));
        Assert.Equal("First", detail.Name);
        Assert.Equal(course.Code, detail.CourseCode);
        Assert.Equal(0, detail.VersionCount);
        await Assert.ThrowsAsync<ForbiddenException>(() => service.GetAsync(outside.Id, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public async Task Create_rejects_blank_names(string name)
    {
        await using var db = CreateDatabase();
        var service = new RubricTemplateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Administrator));

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(
            new CreateRubricTemplateRequest(name, Guid.NewGuid()), CancellationToken.None));

        Assert.Contains("name", exception.Details!.Keys);
        Assert.Empty(db.RubricTemplates);
    }

    [Fact]
    public async Task Create_rejects_name_longer_than_schema_limit()
    {
        await using var db = CreateDatabase();
        var service = new RubricTemplateService(db, new TestCurrentUser(Guid.NewGuid(), Roles.Administrator));

        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(
            new CreateRubricTemplateRequest(new string('x', 201), Guid.NewGuid()), CancellationToken.None));
        Assert.Empty(db.RubricTemplates);
    }

    [Fact]
    public async Task Update_changes_template_name_without_changing_version_history_or_creator()
    {
        await using var db = CreateDatabase();
        var (user, course) = await SeedAssignedCourseAsync(db);
        var template = NewTemplate(user.Id, course, "Original");
        db.RubricTemplates.Add(template);
        await db.SaveChangesAsync();
        var version = new RubricVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            Template = template,
            CourseId = course.Id,
            VersionNo = 1,
            MaxScore = 10m,
            IsSealed = true,
            CreatedBy = user.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.RubricVersions.Add(version);
        await db.SaveChangesAsync();
        var originalCreator = template.CreatedBy;
        var originalCreatedAt = template.CreatedAt;
        var service = new RubricTemplateService(db, new TestCurrentUser(user.Id, Roles.Administrator));

        var updated = await service.UpdateAsync(template.Id, new UpdateRubricTemplateRequest("Renamed"),
            CancellationToken.None);

        Assert.Equal("Renamed", updated.Name);
        Assert.Equal(originalCreator, updated.CreatedBy);
        Assert.Equal(originalCreatedAt, updated.CreatedAt);
        Assert.Equal(1, updated.VersionCount);
        Assert.True((await db.RubricVersions.SingleAsync()).IsSealed);
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(user.Id, audit.UserId);
        Assert.Equal("Update", audit.Action);
        Assert.Equal("RubricTemplate", audit.TargetEntity);
        Assert.Equal(template.Id, audit.TargetId);
    }

    [Fact]
    public async Task Delete_allows_template_without_versions_and_rejects_template_with_versions()
    {
        await using var db = CreateDatabase();
        var (user, course) = await SeedAssignedCourseAsync(db);
        var deletable = NewTemplate(user.Id, course, "No versions");
        var retained = NewTemplate(user.Id, course, "Has version");
        db.RubricTemplates.AddRange(deletable, retained);
        await db.SaveChangesAsync();
        db.RubricVersions.Add(new RubricVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = retained.Id,
            Template = retained,
            CourseId = course.Id,
            VersionNo = 1,
            MaxScore = 10m,
            IsSealed = true,
            CreatedBy = user.Id,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = new RubricTemplateService(db, new TestCurrentUser(user.Id, Roles.Administrator));

        await Assert.ThrowsAsync<ConflictException>(() => service.DeleteAsync(retained.Id, CancellationToken.None));
        Assert.Empty(await db.AuditLogs.ToListAsync());
        await service.DeleteAsync(deletable.Id, CancellationToken.None);

        Assert.Null(await db.RubricTemplates.FindAsync(deletable.Id));
        Assert.NotNull(await db.RubricTemplates.FindAsync(retained.Id));
        Assert.Single(db.RubricVersions);
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(user.Id, audit.UserId);
        Assert.Equal("Delete", audit.Action);
        Assert.Equal("RubricTemplate", audit.TargetEntity);
        Assert.Equal(deletable.Id, audit.TargetId);
    }

    private static AccessConfigDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<(User User, Course Course)> SeedAssignedCourseAsync(
        AccessConfigDbContext db, string code = "TST101")
    {
        var role = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var user = new User
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            FullName = "Assigned Lecturer",
            Email = $"{Guid.NewGuid():N}@example.test",
            Username = Guid.NewGuid().ToString("N"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course { Id = Guid.NewGuid(), Code = code, Name = "Test Course" };
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = user.Id,
            Lecturer = user,
            AssignedAt = DateTime.UtcNow
        });
        db.Roles.Add(role);
        db.Users.Add(user);
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return (user, course);
    }

    private static RubricTemplate NewTemplate(Guid creatorId, Course course, string name) => new()
    {
        Id = Guid.NewGuid(),
        CourseId = course.Id,
        Course = course,
        Name = name,
        CreatedBy = creatorId,
        CreatedAt = DateTime.UtcNow
    };

    private sealed class TestCurrentUser(
        Guid id, string role = Roles.Lecturer, bool isAuthenticated = true) : ICurrentUser
    {
        public bool IsAuthenticated => isAuthenticated;
        public Guid Id => id;
        public string? Role => role;
        public bool IsInRole(string requestedRole) => requestedRole == role;
    }
}
