using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
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

        var sessions = await queries.GetSessionsAsync(null, CancellationToken.None);

        var result = Assert.Single(sessions);
        Assert.Equal(1, result.ParticipantCount);
        Assert.Equal(2, result.TakenCount);
        Assert.Equal(1, result.ToReviewCount);
        Assert.Equal(1, interviewResults.CallCount);
        Assert.Equal(new[] { sessionId }, interviewResults.RequestedSessionIds);
    }

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
                sessionIds.Select(id => new AttemptCounts(id, 2, 1)).ToList());
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
}
