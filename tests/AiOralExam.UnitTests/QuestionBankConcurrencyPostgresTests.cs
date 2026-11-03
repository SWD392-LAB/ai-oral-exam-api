using System.Data.Common;
using AiOralExam.Modules.AccessConfig.Application.QuestionBank;
using AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AiOralExam.UnitTests;

public sealed class QuestionBankConcurrencyPostgresTests
{
    private const string ConnectionStringVariable = "AIVES_F1A1_CONCURRENCY_CONNECTION";
    private const string ConfirmationVariable = "AIVES_F1A1_CONCURRENCY_DISPOSABLE_CONFIRM";
    private const string DisposableDatabaseName = "aives_f1_concurrency_test";

    [PostgreSqlConcurrencyFact]
    public async Task Concurrent_imports_of_normalized_duplicate_content_persist_once()
    {
        var connectionString = GetDedicatedConnectionString();
        var fixture = await SeedCourseAsync(connectionString);
        var barrier = new DuplicateCheckBarrier();

        async Task<Exception?> ImportAsync(string content)
        {
            await using var db = CreateDatabase(connectionString, barrier);
            try
            {
                await new QuestionBankService(db, new TestCurrentUser(fixture.LecturerId))
                    .ImportAsync(fixture.CourseId,
                        new ImportQuestionBankRequest([
                            new ImportQuestionBankItem("Concurrency", BloomLevel.Apply, content)
                        ]),
                        CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var imports = new[]
        {
            ImportAsync("  Same question "),
            ImportAsync("same QUESTION")
        };
        var outcomes = await Task.WhenAll(imports);

        Assert.Single(outcomes, outcome => outcome is null);
        var conflict = Assert.IsType<ConflictException>(Assert.Single(outcomes, outcome => outcome is not null));
        Assert.Equal("question_bank_duplicate", conflict.Code);

        await using var verifyDb = CreateDatabase(connectionString);
        var persisted = await verifyDb.QuestionBankItems.AsNoTracking()
            .Where(item => item.CourseId == fixture.CourseId)
            .ToListAsync();
        Assert.Single(persisted);
        Assert.Single(await verifyDb.QuestionBankRevisions.AsNoTracking()
            .Where(revision => revision.QuestionBankItemId == persisted[0].Id)
            .ToListAsync());
        Assert.Single(await verifyDb.AuditLogs.AsNoTracking().Where(log => log.TargetId == persisted[0].Id).ToListAsync());
    }

    [PostgreSqlConcurrencyFact]
    public async Task Database_duplicate_constraint_is_mapped_for_create_and_update()
    {
        var connectionString = GetDedicatedConnectionString();
        var fixture = await SeedCourseAsync(connectionString);
        Guid existingId;
        Guid editableId;

        await using (var db = CreateDatabase(connectionString))
        {
            var service = new QuestionBankService(db, new TestCurrentUser(fixture.LecturerId));
            existingId = (await service.CreateAsync(fixture.CourseId,
                new CreateQuestionBankItemRequest("Topic", BloomLevel.Apply, "Existing"),
                CancellationToken.None)).Id;
            editableId = (await service.CreateAsync(fixture.CourseId,
                new CreateQuestionBankItemRequest("Topic", BloomLevel.Apply, "Editable"),
                CancellationToken.None)).Id;
        }

        await using (var db = CreateDatabase(connectionString))
        {
            var error = await Assert.ThrowsAsync<ConflictException>(() =>
                new QuestionBankService(db, new TestCurrentUser(fixture.LecturerId))
                    .CreateAsync(fixture.CourseId,
                        new CreateQuestionBankItemRequest("Topic", BloomLevel.Apply, " existing "),
                        CancellationToken.None));
            Assert.Equal("question_bank_duplicate", error.Code);
        }

        await using (var db = CreateDatabase(connectionString))
        {
            var error = await Assert.ThrowsAsync<ConflictException>(() =>
                new QuestionBankService(db, new TestCurrentUser(fixture.LecturerId))
                    .UpdateDraftAsync(fixture.CourseId, editableId,
                        new UpdateQuestionBankItemRequest("Topic", BloomLevel.Apply, "EXISTING"),
                        CancellationToken.None));
            Assert.Equal("question_bank_duplicate", error.Code);
        }

        await using var verifyDb = CreateDatabase(connectionString);
        Assert.Equal(2, await verifyDb.QuestionBankItems.CountAsync(item => item.CourseId == fixture.CourseId));
        Assert.Equal("Existing", (await verifyDb.QuestionBankItems.SingleAsync(item => item.Id == existingId)).Content);

        var otherCourse = await SeedCourseAsync(connectionString);
        await using var otherCourseDb = CreateDatabase(connectionString);
        var otherCourseItem = await new QuestionBankService(
                otherCourseDb, new TestCurrentUser(otherCourse.LecturerId))
            .CreateAsync(otherCourse.CourseId,
                new CreateQuestionBankItemRequest("Topic", BloomLevel.Apply, " existing "),
                CancellationToken.None);
        Assert.Equal("existing", (await otherCourseDb.QuestionBankItems
            .SingleAsync(item => item.Id == otherCourseItem.Id)).Content);
    }

    [PostgreSqlConcurrencyFact]
    public async Task Attach_approved_items_translates_the_id_filter_with_postgres()
    {
        var connectionString = GetDedicatedConnectionString();
        var fixture = await SeedCourseAsync(connectionString);
        var sessionId = Guid.NewGuid();

        await using var db = CreateDatabase(connectionString);
        db.ExamSessions.Add(new ExamSession
        {
            Id = sessionId,
            CourseId = fixture.CourseId,
            CreatedBy = fixture.LecturerId,
            Title = "Question bank attach test",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1),
            TimeLimitPerAnswerSeconds = 120,
            Status = ExamSessionStatus.Draft,
            RubricMode = RubricMode.Legacy
        });
        await db.SaveChangesAsync();

        var service = new QuestionBankService(db, new TestCurrentUser(fixture.LecturerId));
        var item = await service.CreateAsync(fixture.CourseId,
            new CreateQuestionBankItemRequest("Attach", BloomLevel.Apply, "Question to attach"),
            CancellationToken.None);
        await service.ApproveAsync(fixture.CourseId, item.Id, CancellationToken.None);

        var attachedIds = await service.AttachApprovedAsync(sessionId,
            new AttachQuestionBankRequest([item.Id]), CancellationToken.None);

        Assert.Equal(new[] { item.Id }, attachedIds);
        var attachedQuestion = await db.Questions.SingleAsync(question => question.ExamSessionId == sessionId);
        Assert.Equal("Question to attach", attachedQuestion.Content);
        Assert.Equal(item.Revisions.Single().Id, attachedQuestion.SourceBankRevisionId);
    }

    private static AccessConfigDbContext CreateDatabase(
        string connectionString,
        DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new AccessConfigDbContext(options.Options);
    }

    private static async Task<Fixture> SeedCourseAsync(string connectionString)
    {
        var fixture = new Fixture(Guid.NewGuid(), Guid.NewGuid());
        await using var db = CreateDatabase(connectionString);
        await AssertDedicatedTargetAsync(db);

        var lecturerRole = await db.Roles.SingleAsync(role => role.Name == Roles.Lecturer);
        var lecturer = new User
        {
            Id = fixture.LecturerId,
            RoleId = lecturerRole.Id,
            Role = lecturerRole,
            FullName = "Question bank concurrency test lecturer",
            Email = $"{fixture.LecturerId:N}@question-bank-concurrency.test",
            Username = $"qb_{fixture.LecturerId:N}",
            LecturerCode = fixture.LecturerId.ToString("N")[..20],
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course
        {
            Id = fixture.CourseId,
            Code = $"QB{fixture.CourseId:N}"[..20],
            Name = "Question bank concurrency test course"
        };
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = lecturer.Id,
            Lecturer = lecturer,
            AssignedAt = DateTime.UtcNow
        });

        db.Users.Add(lecturer);
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return fixture;
    }

    private static string GetDedicatedConnectionString()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(ConfirmationVariable), "YES", StringComparison.Ordinal))
            throw new InvalidOperationException($"Set {ConfirmationVariable}=YES only for the disposable concurrency database.");

        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Set {ConnectionStringVariable} to the disposable PostgreSQL concurrency database connection string.");

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

    private sealed record Fixture(Guid LecturerId, Guid CourseId);

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string role) => role == Roles.Lecturer;
    }

    private sealed class DuplicateCheckBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothQueriesReachedCheck =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("question_bank_items", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("content_hash", StringComparison.OrdinalIgnoreCase))
            {
                if (Interlocked.Increment(ref _arrived) == 2)
                    _bothQueriesReachedCheck.TrySetResult();
                await _bothQueriesReachedCheck.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
