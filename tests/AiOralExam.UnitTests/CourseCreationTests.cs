using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Application.LecturerAssignments;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Controllers;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiOralExam.UnitTests;

public class CourseCreationTests
{
    [Fact]
    public async Task Create_assigns_the_valid_lecturer_and_audits_both_operations()
    {
        await using var db = CreateDbContext();
        var lecturer = AddUser(db, UserRole.Lecturer);
        await db.SaveChangesAsync();
        var currentUser = new TestCurrentUser(Guid.NewGuid());
        var assignments = new LecturerAssignmentService(db, currentUser);
        var courses = new CourseService(db, currentUser, assignments);

        var created = await courses.CreateAsync(
            new CreateCourseRequest("cs101", "Course 101", lecturer.Id), CancellationToken.None);

        Assert.Equal("CS101", created.Code);
        Assert.Equal(1, created.LecturerCount);
        Assert.Equal(lecturer.Id, Assert.Single(db.CourseLecturers).LecturerId);
        Assert.Equal(new[] { AuditActions.AssignLecturer, AuditActions.Create },
            db.AuditLogs.Select(x => x.Action).OrderBy(x => x).ToArray());
        var courseAudit = Assert.Single(db.AuditLogs.Where(x => x.Action == AuditActions.Create));
        Assert.Equal(AuditEntities.Course, courseAudit.TargetEntity);
        Assert.Contains("CS101", courseAudit.Description);
    }

    [Fact]
    public async Task Create_rejects_a_non_lecturer_without_persisting_a_course()
    {
        await using var db = CreateDbContext();
        var student = AddUser(db, UserRole.Student);
        await db.SaveChangesAsync();
        var currentUser = new TestCurrentUser(Guid.NewGuid());
        var assignments = new LecturerAssignmentService(db, currentUser);
        var courses = new CourseService(db, currentUser, assignments);

        await Assert.ThrowsAsync<ValidationException>(() => courses.CreateAsync(
            new CreateCourseRequest("cs101", "Course 101", student.Id), CancellationToken.None));

        Assert.Empty(db.Courses);
        Assert.Empty(db.CourseLecturers);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Create_rejects_an_empty_lecturer_id_without_persisting_anything()
    {
        await using var db = CreateDbContext();
        var currentUser = new TestCurrentUser(Guid.NewGuid());
        var assignments = new LecturerAssignmentService(db, currentUser);
        var courses = new CourseService(db, currentUser, assignments);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => courses.CreateAsync(
            new CreateCourseRequest("cs101", "Course 101", Guid.Empty), CancellationToken.None));

        Assert.Equal("A lecturer ID is required.", Assert.Single(exception.Details!["lecturerId"]));
        Assert.Empty(db.Courses);
        Assert.Empty(db.CourseLecturers);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Create_rejects_an_inactive_lecturer_without_persisting_anything()
    {
        await using var db = CreateDbContext();
        var lecturer = AddUser(db, UserRole.Lecturer);
        lecturer.IsActive = false;
        await db.SaveChangesAsync();
        var currentUser = new TestCurrentUser(Guid.NewGuid());
        var assignments = new LecturerAssignmentService(db, currentUser);
        var courses = new CourseService(db, currentUser, assignments);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => courses.CreateAsync(
            new CreateCourseRequest("cs101", "Course 101", lecturer.Id), CancellationToken.None));

        Assert.Equal("Lecturer must be active.", Assert.Single(exception.Details!["lecturerId"]));
        Assert.Empty(db.Courses);
        Assert.Empty(db.CourseLecturers);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Admin_course_list_includes_ordered_lecturers_and_empty_lists()
    {
        await using var db = CreateDbContext();
        var courseWithLecturers = new Course
        {
            Id = Guid.NewGuid(),
            Code = "CS101",
            Name = "Course 101"
        };
        var courseWithoutLecturers = new Course
        {
            Id = Guid.NewGuid(),
            Code = "CS102",
            Name = "Course 102"
        };
        var zLecturer = AddUser(db, UserRole.Lecturer);
        zLecturer.FullName = "Zed Lecturer";
        var aLecturer = AddUser(db, UserRole.Lecturer);
        aLecturer.FullName = "Ada Lecturer";
        db.Courses.AddRange(courseWithLecturers, courseWithoutLecturers);
        db.CourseLecturers.AddRange(
            new CourseLecturer
            {
                Course = courseWithLecturers,
                Lecturer = zLecturer,
                CourseId = courseWithLecturers.Id,
                LecturerId = zLecturer.Id,
                AssignedAt = DateTime.UtcNow
            },
            new CourseLecturer
            {
                Course = courseWithLecturers,
                Lecturer = aLecturer,
                CourseId = courseWithLecturers.Id,
                LecturerId = aLecturer.Id,
                AssignedAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        var currentUser = new TestCurrentUser(Guid.NewGuid());
        var courses = new CourseService(db, currentUser, new LecturerAssignmentService(db, currentUser));

        var result = await new AdminCoursesController(courses).List(CancellationToken.None);

        var courseWithLecturersResult = Assert.Single(result, x => x.Id == courseWithLecturers.Id);
        Assert.Equal(new[] { "Ada Lecturer", "Zed Lecturer" },
            courseWithLecturersResult.Lecturers.Select(x => x.FullName));
        var courseWithoutLecturersResult = Assert.Single(result, x => x.Id == courseWithoutLecturers.Id);
        Assert.Empty(courseWithoutLecturersResult.Lecturers);
    }

    private static AccessConfigDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AccessConfigDbContext(options);
    }

    private static User AddUser(AccessConfigDbContext db, UserRole role)
    {
        var roleEntity = new Role { Id = Guid.NewGuid(), Name = role.ToString() };
        var user = new User
        {
            Id = Guid.NewGuid(),
            RoleId = roleEntity.Id,
            Role = roleEntity,
            FullName = "Test User",
            Email = $"{Guid.NewGuid():N}@example.test",
            Username = Guid.NewGuid().ToString("N")
        };
        db.Roles.Add(roleEntity);
        db.Users.Add(user);
        return user;
    }

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Administrator;
        public bool IsInRole(string role) => role == Roles.Administrator;
    }
}
