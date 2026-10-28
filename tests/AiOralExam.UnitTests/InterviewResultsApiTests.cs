using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AiOralExam.UnitTests;

public class InterviewResultsApiTests
{
    [Fact]
    public void Attempt_result_exposes_and_serializes_ai_suggested_total_separately_from_final_score()
    {
        var result = new AttemptResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AttemptStatus.Finalized,
            DateTime.UtcNow, DateTime.UtcNow,
            new ScoreReviewResult(Guid.NewGuid(), 6m, null, ReviewStatus.Approved, DateTime.UtcNow),
            [
                new QuestionResponseResult(Guid.NewGuid(), Guid.NewGuid(), 0, true, 4m, null, []),
                new QuestionResponseResult(Guid.NewGuid(), Guid.NewGuid(), 0, true, 3.5m, null, []),
            ]);

        Assert.Equal(7.5m, result.SuggestedTotalScore);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var response = document.RootElement;
        Assert.Equal(7.5m, response.GetProperty("suggestedTotalScore").GetDecimal());
        Assert.Equal(6m, response.GetProperty("review").GetProperty("finalScore").GetDecimal());
    }

    [Fact]
    public void Attempt_result_suggested_total_is_null_when_any_evaluation_is_incomplete()
    {
        var result = new AttemptResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AttemptStatus.InProgress,
            DateTime.UtcNow, null, null,
            [new QuestionResponseResult(Guid.NewGuid(), Guid.NewGuid(), 0, false, null, null, [])]);

        Assert.Null(result.SuggestedTotalScore);
    }

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

        Assert.Equal(new AttemptCounts(noAttemptsSessionId, 0, 0, 0), counts[noAttemptsSessionId]);
        Assert.Equal(new AttemptCounts(pendingSessionId, 1, 1, 1), counts[pendingSessionId]);
        Assert.Equal(new AttemptCounts(finalizedSessionId, 1, 0, 0), counts[finalizedSessionId]);
    }

    private static InterviewAttempt AttemptFor(Participant participant)
        => new()
        {
            Id = Guid.NewGuid(),
            ParticipantId = participant.Id,
            Participant = participant
        };
}
