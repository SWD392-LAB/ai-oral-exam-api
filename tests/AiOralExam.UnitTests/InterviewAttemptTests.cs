using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.UnitTests;

public class InterviewAttemptTests
{
    [Theory]
    [InlineData(AttemptStatus.InProgress, AttemptStatus.PendingReview, true)]
    [InlineData(AttemptStatus.PendingReview, AttemptStatus.Finalized, true)]
    [InlineData(AttemptStatus.InProgress, AttemptStatus.Finalized, false)]
    [InlineData(AttemptStatus.Finalized, AttemptStatus.InProgress, false)]
    [InlineData(AttemptStatus.PendingReview, AttemptStatus.InProgress, false)]
    public void CanTransition_follows_state_machine(AttemptStatus from, AttemptStatus to, bool expected) =>
        Assert.Equal(expected, InterviewAttempt.CanTransition(from, to));

    [Fact]
    public void Complete_twice_is_rejected()
    {
        var attempt = new InterviewAttempt { Id = Guid.NewGuid() };
        attempt.Complete(DateTime.UtcNow);

        Assert.Equal(AttemptStatus.PendingReview, attempt.Status);
        Assert.Throws<ConflictException>(() => attempt.Complete(DateTime.UtcNow));
    }

    [Fact]
    public void AddTurn_numbers_turns_and_counts_follow_ups()
    {
        var response = new QuestionResponse { Id = Guid.NewGuid() };
        response.AddTurn(TurnType.Main, "Q1", DateTime.UtcNow);
        var followUp = response.AddTurn(TurnType.FollowUp, "Q1 - follow up", DateTime.UtcNow);

        Assert.Equal(2, followUp.TurnNo);
        Assert.Equal(1, response.FollowUpCount);
        Assert.Same(followUp, response.CurrentTurn);
    }
}
