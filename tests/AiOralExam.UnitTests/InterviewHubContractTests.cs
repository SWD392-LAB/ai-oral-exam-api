using AiOralExam.Modules.Interview.Hubs;

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
}
