using AiOralExam.Modules.AccessConfig.Application.RubricTemplates;
using AiOralExam.Modules.AccessConfig.Application.RubricVersions;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiOralExam.UnitTests;

public sealed class RubricConcurrencyPostgresTests
{
    private const string ConnectionStringVariable = "AIVES_F1A1_CONCURRENCY_CONNECTION";
    private const string ConfirmationVariable = "AIVES_F1A1_CONCURRENCY_DISPOSABLE_CONFIRM";
    private const string DisposableDatabaseName = "aives_f1_concurrency_test";

    [PostgreSqlConcurrencyFact]
    public async Task Concurrent_version_creation_allocates_sequential_unique_numbers()
    {
        var connectionString = GetDedicatedConnectionString();
        var fixture = await SeedTemplateAsync(connectionString);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RubricVersionDetailDto> CreateAsync()
        {
            await gate.Task;
            await using var db = CreateDatabase(connectionString);
            return await new RubricVersionService(db, new TestCurrentUser(fixture.LecturerId))
                .CreateAsync(fixture.TemplateId, ValidRequest(), CancellationToken.None);
        }

        var firstRequest = CreateAsync();
        var secondRequest = CreateAsync();
        gate.SetResult();
        var created = await Task.WhenAll(firstRequest, secondRequest);

        Assert.Equal(new[] { 1, 2 }, created.Select(x => x.VersionNo).Order());
        Assert.Equal(2, created.Select(x => x.Id).Distinct().Count());
        Assert.All(created, version =>
        {
            Assert.True(version.IsSealed);
            Assert.Equal(10m, version.MaxScore);
            Assert.Equal(2, version.Criteria.Count);
        });

        await using var verifyDb = CreateDatabase(connectionString);
        var persisted = await verifyDb.RubricVersions.AsNoTracking()
            .Include(x => x.Criteria)
            .Where(x => x.TemplateId == fixture.TemplateId)
            .OrderBy(x => x.VersionNo)
            .ToListAsync();

        Assert.Equal(new[] { 1, 2 }, persisted.Select(x => x.VersionNo));
        Assert.Equal(2, persisted.Select(x => x.Id).Distinct().Count());
        Assert.All(persisted, version =>
        {
            Assert.True(version.IsSealed);
            Assert.Equal(10m, version.MaxScore);
            Assert.Equal(2, version.Criteria.Count);
            Assert.Equal(version.MaxScore, version.Criteria.Sum(x => x.MaxPoints));
            Assert.All(version.Criteria, criterion =>
            {
                Assert.Equal(version.Id, criterion.RubricVersionId);
                Assert.Null(criterion.RubricId);
            });
        });
    }

    [PostgreSqlConcurrencyFact]
    public async Task Template_deletion_racing_version_creation_has_one_consistent_winner()
    {
        var connectionString = GetDedicatedConnectionString();
        var fixture = await SeedTemplateAsync(connectionString);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<CreateOutcome> CreateAsync()
        {
            await gate.Task;
            await using var db = CreateDatabase(connectionString);
            try
            {
                var version = await new RubricVersionService(db, new TestCurrentUser(fixture.LecturerId))
                    .CreateAsync(fixture.TemplateId, ValidRequest(), CancellationToken.None);
                return new CreateOutcome(version, null);
            }
            catch (NotFoundException exception)
            {
                return new CreateOutcome(null, exception);
            }
        }

        async Task<DeleteOutcome> DeleteAsync()
        {
            await gate.Task;
            await using var db = CreateDatabase(connectionString);
            try
            {
                await new RubricTemplateService(db, new TestCurrentUser(fixture.LecturerId))
                    .DeleteAsync(fixture.TemplateId, CancellationToken.None);
                return new DeleteOutcome(true, null);
            }
            catch (ConflictException exception)
            {
                return new DeleteOutcome(false, exception);
            }
        }

        var createRequest = CreateAsync();
        var deleteRequest = DeleteAsync();
        gate.SetResult();
        await Task.WhenAll((Task)createRequest, deleteRequest);
        var createOutcome = await createRequest;
        var deleteOutcome = await deleteRequest;

        await using var verifyDb = CreateDatabase(connectionString);
        var templateExists = await verifyDb.RubricTemplates.AsNoTracking()
            .AnyAsync(x => x.Id == fixture.TemplateId);
        var persistedVersions = await verifyDb.RubricVersions.AsNoTracking()
            .Where(x => x.TemplateId == fixture.TemplateId)
            .ToListAsync();

        if (createOutcome.Version is not null)
        {
            Assert.IsType<ConflictException>(deleteOutcome.Exception);
            Assert.True(templateExists);
            var version = Assert.Single(persistedVersions);
            Assert.Equal(1, version.VersionNo);
            Assert.True(version.IsSealed);
            Assert.Equal(10m, version.MaxScore);
        }
        else
        {
            Assert.IsType<NotFoundException>(createOutcome.Exception);
            Assert.True(deleteOutcome.Deleted);
            Assert.Null(deleteOutcome.Exception);
            Assert.False(templateExists);
            Assert.Empty(persistedVersions);
        }
    }

    private static CreateRubricVersionRequest ValidRequest() =>
        new([new(1, "Accuracy", 5m), new(2, "Clarity", 5m)]);

    private static AccessConfigDbContext CreateDatabase(string connectionString) =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    private static async Task<Fixture> SeedTemplateAsync(string connectionString)
    {
        var ids = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = CreateDatabase(connectionString);
        await AssertDedicatedTargetAsync(db);

        var lecturerRole = await db.Roles.SingleAsync(x => x.Name == Roles.Lecturer);
        var lecturer = new User
        {
            Id = ids.LecturerId,
            RoleId = lecturerRole.Id,
            Role = lecturerRole,
            FullName = "F1A1 concurrency test lecturer",
            Email = $"{ids.LecturerId:N}@f1a1-concurrency.test",
            Username = $"f1a1_{ids.LecturerId:N}",
            LecturerCode = ids.LecturerId.ToString("N")[..20],
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course
        {
            Id = ids.CourseId,
            Code = ($"F1A1{ids.CourseId:N}")[..20],
            Name = "F1A1 concurrency test course"
        };
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = lecturer.Id,
            Lecturer = lecturer,
            AssignedAt = DateTime.UtcNow
        });
        var template = new RubricTemplate
        {
            Id = ids.TemplateId,
            CourseId = course.Id,
            Course = course,
            Name = $"F1A1 concurrency {ids.TemplateId:N}",
            CreatedBy = lecturer.Id,
            CreatedAt = DateTime.UtcNow
        };

        db.Users.Add(lecturer);
        db.Courses.Add(course);
        db.RubricTemplates.Add(template);
        await db.SaveChangesAsync();
        return ids;
    }

    private static string GetDedicatedConnectionString()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(ConfirmationVariable), "YES", StringComparison.Ordinal))
            throw new InvalidOperationException($"Set {ConfirmationVariable}=YES only for the disposable concurrency database.");

        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Set {ConnectionStringVariable} to the disposable concurrency database connection string.");

        var parsed = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(parsed.Database, DisposableDatabaseName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Concurrency tests only run against database {DisposableDatabaseName}.");

        return connectionString;
    }

    private static async Task AssertDedicatedTargetAsync(AccessConfigDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT current_database(), current_schema()";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(DisposableDatabaseName, reader.GetString(0));
        Assert.Equal("fresh", reader.GetString(1));
    }

    private sealed record Fixture(Guid LecturerId, Guid CourseId, Guid TemplateId);
    private sealed record CreateOutcome(RubricVersionDetailDto? Version, Exception? Exception);
    private sealed record DeleteOutcome(bool Deleted, Exception? Exception);

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string requestedRole) => requestedRole == Roles.Lecturer;
    }
}

public sealed class PostgreSqlConcurrencyFactAttribute : FactAttribute
{
    public PostgreSqlConcurrencyFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIVES_F1A1_CONCURRENCY_CONNECTION"))
            || !string.Equals(
                Environment.GetEnvironmentVariable("AIVES_F1A1_CONCURRENCY_DISPOSABLE_CONFIRM"),
                "YES",
                StringComparison.Ordinal))
            Skip = "Requires explicit opt-in and a dedicated disposable PostgreSQL concurrency database.";
    }
}
