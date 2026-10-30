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
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), 1, "answer.accepted", DateTime.UtcNow);
        var transcriptEvent = new TranscriptUpdatedEvent(Guid.NewGuid(), Guid.NewGuid(), "accepted transcript");

        var payload = JsonSerializer.Serialize(new { attemptEvent, transcriptEvent });

        Assert.DoesNotContain("score", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("feedback", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("criterion", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evaluation", payload, StringComparison.OrdinalIgnoreCase);
    }
}
