using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.UnitTests;

public class InterviewAttemptTests
{
    [Theory]
    [InlineData(AttemptStatus.InProgress, AttemptStatus.PendingReview, true)]
    [InlineData(AttemptStatus.InProgress, AttemptStatus.Interrupted, true)]
    [InlineData(AttemptStatus.PendingReview, AttemptStatus.Finalized, true)]
    [InlineData(AttemptStatus.InProgress, AttemptStatus.Finalized, false)]
    [InlineData(AttemptStatus.Finalized, AttemptStatus.InProgress, false)]
    [InlineData(AttemptStatus.PendingReview, AttemptStatus.InProgress, false)]
    [InlineData(AttemptStatus.Interrupted, AttemptStatus.PendingReview, true)]
    [InlineData(AttemptStatus.Interrupted, AttemptStatus.InProgress, false)]
    public void CanTransition_follows_state_machine(AttemptStatus from, AttemptStatus to, bool expected) =>
        Assert.Equal(expected, InterviewAttempt.CanTransition(from, to));

    [Fact]
    public void Interrupt_closes_an_attempt_without_marking_it_ready_for_review()
    {
        var completedAt = DateTime.UtcNow;
        var attempt = new InterviewAttempt { Id = Guid.NewGuid() };

        attempt.Interrupt(completedAt);

        Assert.Equal(AttemptStatus.Interrupted, attempt.Status);
        Assert.Equal(completedAt, attempt.CompletedAt);
    }

    [Fact]
    public void Complete_twice_is_rejected()
    {
        var attempt = new InterviewAttempt { Id = Guid.NewGuid() };
        attempt.Complete(DateTime.UtcNow);

        Assert.Equal(AttemptStatus.PendingReview, attempt.Status);
        Assert.Throws<ConflictException>(() => attempt.Complete(DateTime.UtcNow));
    }

    [Fact]
    public void Participant_status_reconciles_attempt_history_without_regressing_completed_attempts()
    {
        var now = DateTime.UtcNow;
        var participant = new Participant();
        var interrupted = new InterviewAttempt { Id = Guid.NewGuid() };
        interrupted.Interrupt(now);
        participant.Attempts.Add(interrupted);

        participant.ReconcileStatus();

        Assert.Equal(ParticipantStatus.Registered, participant.Status);

        var completed = new InterviewAttempt { Id = Guid.NewGuid() };
        completed.Complete(now);
        participant.Attempts.Add(completed);
        participant.ReconcileStatus();
        Assert.Equal(ParticipantStatus.Completed, participant.Status);

        var active = new InterviewAttempt { Id = Guid.NewGuid() };
        participant.Attempts.Add(active);
        participant.ReconcileStatus();
        Assert.Equal(ParticipantStatus.InProgress, participant.Status);

        active.Interrupt(now);
        participant.ReconcileStatus();
        Assert.Equal(ParticipantStatus.Completed, participant.Status);
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

    [Fact]
    public void Manual_recovery_resets_only_the_exhausted_ai_retry_counter_and_preserves_answer_data()
    {
        var answeredAt = DateTime.UtcNow;
        var audioAcceptedAt = answeredAt.AddSeconds(-1);
        var turn = new InterviewTurn
        {
            ProcessingStatus = TurnProcessingStatus.Failed,
            RetryCount = 4,
            TranscriptionRetryCount = 2,
            AnswerTranscript = "accepted transcript",
            AnsweredAt = answeredAt,
            AudioAcceptedAt = audioAcceptedAt,
            SubmissionResultJson = "{\"result\":true}"
        };

        turn.PrepareManualRecovery(AttemptStatus.Interrupted);

        Assert.Equal(0, turn.RetryCount);
        Assert.Equal(2, turn.TranscriptionRetryCount);
        Assert.Equal(1, turn.ManualRecoveryCount);
        Assert.Equal(TurnProcessingStatus.Retry, turn.ProcessingStatus);
        Assert.Equal("accepted transcript", turn.AnswerTranscript);
        Assert.Equal(answeredAt, turn.AnsweredAt);
        Assert.Equal(audioAcceptedAt, turn.AudioAcceptedAt);
        Assert.Equal("{\"result\":true}", turn.SubmissionResultJson);
    }

    [Fact]
    public void Manual_recovery_resets_the_transcription_retry_counter_for_an_accepted_audio_turn()
    {
        var turn = new InterviewTurn
        {
            ProcessingStatus = TurnProcessingStatus.Failed,
            AudioAcceptedAt = DateTime.UtcNow,
            TranscriptionRetryCount = 3,
            RetryCount = 2
        };

        turn.PrepareManualRecovery(AttemptStatus.Interrupted);

        Assert.Equal(0, turn.TranscriptionRetryCount);
        Assert.Equal(2, turn.RetryCount);
    }

    [Fact]
    public void Manual_recovery_is_rejected_after_the_single_recovery_budget_is_used()
    {
        var turn = new InterviewTurn
        {
            ProcessingStatus = TurnProcessingStatus.Failed
        };

        turn.PrepareManualRecovery(AttemptStatus.Interrupted);
        turn.ProcessingStatus = TurnProcessingStatus.Failed;

        var exception = Assert.Throws<ConflictException>(
            () => turn.PrepareManualRecovery(AttemptStatus.Interrupted));

        Assert.Equal("manual_recovery_already_used", exception.Code);
    }
}
