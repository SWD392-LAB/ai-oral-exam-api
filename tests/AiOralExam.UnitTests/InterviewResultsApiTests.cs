using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.UnitTests;

public class InterviewResultsApiTests
{
    [Fact]
    public async Task Get_attempt_counts_groups_by_session_and_returns_zeroes_for_sessions_without_attempts()
    {
        await using var db = new InterviewDbContext(new DbContextOptionsBuilder<InterviewDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var noAttemptsSessionId = Guid.NewGuid();
        var pendingSessionId = Guid.NewGuid();
        var finalizedSessionId = Guid.NewGuid();
        var pendingParticipant = new Participant { Id = Guid.NewGuid(), ExamSessionId = pendingSessionId };
        var finalizedParticipant = new Participant { Id = Guid.NewGuid(), ExamSessionId = finalizedSessionId };
        var pendingAttempt = AttemptFor(pendingParticipant);
        pendingAttempt.Complete(DateTime.UtcNow);
        var inProgressAttempt = AttemptFor(pendingParticipant);
        var finalizedAttempt = AttemptFor(finalizedParticipant);
        finalizedAttempt.Complete(DateTime.UtcNow);
        finalizedAttempt.TransitionTo(AttemptStatus.Finalized);
        db.Participants.AddRange(pendingParticipant, finalizedParticipant);
        db.InterviewAttempts.AddRange(pendingAttempt, inProgressAttempt, finalizedAttempt);
        await db.SaveChangesAsync();

        var result = await new InterviewResultsApi(db).GetAttemptCountsAsync(
            [noAttemptsSessionId, pendingSessionId, finalizedSessionId], CancellationToken.None);
        var counts = result.ToDictionary(x => x.ExamSessionId);

        Assert.Equal(new AttemptCounts(noAttemptsSessionId, 0, 0), counts[noAttemptsSessionId]);
        Assert.Equal(new AttemptCounts(pendingSessionId, 1, 1), counts[pendingSessionId]);
        Assert.Equal(new AttemptCounts(finalizedSessionId, 1, 0), counts[finalizedSessionId]);
    }

    private static InterviewAttempt AttemptFor(Participant participant)
        => new()
        {
            Id = Guid.NewGuid(),
            ParticipantId = participant.Id,
            Participant = participant
        };
}
