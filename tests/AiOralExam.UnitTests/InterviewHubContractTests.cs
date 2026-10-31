using AiOralExam.Modules.Interview.Hubs;
using AiOralExam.Modules.Interview.Application.Events;
using System.Text.Json;

namespace AiOralExam.UnitTests;

public sealed class InterviewHubContractTests
{
    [Fact]
    public void Hub_exposes_join_chunk_checkpoint_and_answer_completion_commands()
    {
        Assert.NotNull(typeof(InterviewUpdatesHub).GetMethod("JoinAttempt"));
        var completeAnswer = typeof(InterviewUpdatesHub).GetMethod("CompleteAnswer");
        Assert.NotNull(completeAnswer);
        Assert.Equal(new[] { "attemptId", "turnId", "answerText" },
            completeAnswer!.GetParameters().Select(parameter => parameter.Name));

        var sendAudioChunk = typeof(InterviewUpdatesHub).GetMethod("SendAudioChunk");
        Assert.NotNull(sendAudioChunk);
        Assert.Equal(new[] { "attemptId", "turnId", "sequence", "chunk", "contentType" },
            sendAudioChunk!.GetParameters().Select(parameter => parameter.Name));
        Assert.NotNull(typeof(InterviewUpdatesHub).GetMethod("GetAudioCheckpoint"));
    }

    [Fact]
    public void Student_realtime_payloads_do_not_contain_ai_evaluation_fields()
    {
        var attemptEvent = new InterviewAttemptEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), 1, InterviewEventTypes.AnswerAccepted, DateTime.UtcNow);
        var transcriptEvent = new TranscriptUpdatedEvent(Guid.NewGuid(), Guid.NewGuid(), "accepted transcript");

        var payload = JsonSerializer.Serialize(new { attemptEvent, transcriptEvent });

        Assert.DoesNotContain("score", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("feedback", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("criterion", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evaluation", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Interview_event_type_constants_preserve_the_wire_values()
    {
        Assert.Equal(
            [
                "attempt.started",
                "attempt.resumed",
                "turn.opened",
                "answer.accepted",
                "answer.duplicate",
                "turn.deadline_reached",
                "turn.closed",
                "attempt.completed",
                "attempt.expired",
                "review.finalized",
                "InterviewError",
            ],
            new[]
            {
                InterviewEventTypes.AttemptStarted,
                InterviewEventTypes.AttemptResumed,
                InterviewEventTypes.TurnOpened,
                InterviewEventTypes.AnswerAccepted,
                InterviewEventTypes.AnswerDuplicate,
                InterviewEventTypes.TurnDeadlineReached,
                InterviewEventTypes.TurnClosed,
                InterviewEventTypes.AttemptCompleted,
                InterviewEventTypes.AttemptExpired,
                InterviewEventTypes.ReviewFinalized,
                InterviewEventTypes.InterviewError,
            });
    }
}
