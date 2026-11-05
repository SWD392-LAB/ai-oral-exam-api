using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.UnitTests;

public class ExamSessionQueriesTests
{
    [Fact]
    public async Task Get_sessions_merges_participant_and_attempt_counts()
    {
        await using var db = new AccessConfigDbContext(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var role = new Role { Id = Guid.NewGuid(), Name = Roles.Student };
        var student = new User
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            FullName = "Student",
            Email = "student@example.test",
            Username = "student",
            IsActive = true
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "SWD392", Name = "Software Development" };
        var sessionId = Guid.NewGuid();
        var session = new ExamSession
        {
            Id = sessionId,
            CourseId = course.Id,
            Course = course,
            CreatedBy = student.Id,
            Title = "Session",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1),
            TimeLimitPerAnswerSeconds = 120
        };
        db.Participants.Add(new Participant
        {
            Id = Guid.NewGuid(),
            ExamSessionId = session.Id,
            ExamSession = session,
            StudentId = student.Id,
            Student = student
        });
        await db.SaveChangesAsync();
        var interviewResults = new FakeInterviewResultsApi();
        var queries = new ExamSessionQueries(db, new AdministratorCurrentUser(), interviewResults);

        var sessions = await queries.GetSessionsAsync(null, 1, 10, CancellationToken.None);

        var result = Assert.Single(sessions.Items);
        Assert.Equal(1, sessions.TotalCount);
        Assert.Equal(1, sessions.Page);
        Assert.Equal(10, sessions.PageSize);
        Assert.Equal(1, sessions.TotalPages);
        Assert.Equal(1, result.ParticipantCount);
        Assert.Equal(2, result.TakenCount);
        Assert.Equal(1, result.ToReviewCount);
        Assert.Equal(3, result.InProgressCount);
        Assert.Equal(1, interviewResults.CallCount);
        Assert.Equal(new[] { sessionId }, interviewResults.RequestedSessionIds);
    }

    [Fact]
    public async Task Get_sessions_paginates_filters_and_orders_stably()
    {
        await using var db = new AccessConfigDbContext(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var firstCourse = new Course { Id = Guid.NewGuid(), Code = "A", Name = "First" };
        var secondCourse = new Course { Id = Guid.NewGuid(), Code = "B", Name = "Second" };
        var sharedStart = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var oldestId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var newestId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var tieBreakingId = Guid.Parse("00000000-0000-0000-0000-000000000003");

        db.Courses.AddRange(firstCourse, secondCourse);
        db.ExamSessions.AddRange(
            Session(oldestId, firstCourse, sharedStart.AddDays(-1)),
            Session(newestId, firstCourse, sharedStart),
            Session(tieBreakingId, secondCourse, sharedStart));
        await db.SaveChangesAsync();

        var queries = new ExamSessionQueries(db, new AdministratorCurrentUser(), new FakeInterviewResultsApi());

        var page1 = await queries.GetSessionsAsync(null, 1, 2, CancellationToken.None);
        var page2 = await queries.GetSessionsAsync(null, 2, 2, CancellationToken.None);
        var filtered = await queries.GetSessionsAsync(secondCourse.Id, 1, 10, CancellationToken.None);

        Assert.Equal([newestId, tieBreakingId], page1.Items.Select(x => x.Id));
        Assert.Equal([oldestId], page2.Items.Select(x => x.Id));
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(1, page1.Page);
        Assert.Equal(2, page1.PageSize);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal([tieBreakingId], filtered.Items.Select(x => x.Id));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task Get_sessions_rejects_invalid_pagination(int page, int pageSize)
    {
        await using var db = new AccessConfigDbContext(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var queries = new ExamSessionQueries(db, new AdministratorCurrentUser(), new FakeInterviewResultsApi());

        await Assert.ThrowsAsync<AiOralExam.SharedKernel.Errors.ValidationException>(() =>
            queries.GetSessionsAsync(null, page, pageSize, CancellationToken.None));
    }

    [Fact]
    public async Task Get_sessions_keeps_lecturer_access_restriction()
    {
        await using var db = new AccessConfigDbContext(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var lecturerId = Guid.NewGuid();
        var assignedCourse = new Course { Id = Guid.NewGuid(), Code = "ASSIGNED", Name = "Assigned" };
        assignedCourse.Lecturers.Add(new CourseLecturer
        {
            CourseId = assignedCourse.Id,
            Course = assignedCourse,
            LecturerId = lecturerId
        });
        var unassignedCourse = new Course { Id = Guid.NewGuid(), Code = "OTHER", Name = "Other" };
        db.Courses.AddRange(assignedCourse, unassignedCourse);
        db.ExamSessions.AddRange(
            Session(Guid.NewGuid(), assignedCourse, DateTime.UtcNow),
            Session(Guid.NewGuid(), unassignedCourse, DateTime.UtcNow.AddMinutes(1)));
        await db.SaveChangesAsync();

        var result = await new ExamSessionQueries(
            db, new LecturerCurrentUser(lecturerId), new FakeInterviewResultsApi())
            .GetSessionsAsync(null, 1, 10, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("ASSIGNED", item.CourseCode);
        Assert.Equal(1, result.TotalCount);
    }

    private static ExamSession Session(Guid id, Course course, DateTime startTime) => new()
    {
        Id = id,
        CourseId = course.Id,
        Course = course,
        Title = id.ToString(),
        StartTime = startTime,
        EndTime = startTime.AddHours(1),
        TimeLimitPerAnswerSeconds = 120
    };

    private sealed class FakeInterviewResultsApi : IInterviewResultsApi
    {
        public int CallCount { get; private set; }
        public IReadOnlyCollection<Guid> RequestedSessionIds { get; private set; } = [];

        public Task<AttemptResult?> GetAttemptResultAsync(Guid attemptId, CancellationToken ct = default)
            => Task.FromResult<AttemptResult?>(null);

        public Task<IReadOnlyList<AttemptCounts>> GetAttemptCountsAsync(
            IReadOnlyCollection<Guid> sessionIds, CancellationToken ct = default)
        {
            CallCount++;
            RequestedSessionIds = sessionIds.ToArray();
            return Task.FromResult<IReadOnlyList<AttemptCounts>>(
                sessionIds.Select(id => new AttemptCounts(id, 2, 1, 3)).ToList());
        }

        public Task<IReadOnlyList<AttemptResult>> GetSessionResultsAsync(Guid examSessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AttemptResult>>([]);
    }

    private sealed class AdministratorCurrentUser : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => Guid.NewGuid();
        public string? Role => Roles.Administrator;
        public bool IsInRole(string role) => role == Roles.Administrator;
    }

    private sealed class LecturerCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string role) => role == Roles.Lecturer;
    }
}
