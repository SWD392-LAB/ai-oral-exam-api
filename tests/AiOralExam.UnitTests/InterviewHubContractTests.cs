using AiOralExam.Modules.Interview.Hubs;

namespace AiOralExam.UnitTests;

public sealed class InterviewHubContractTests
{
    [Fact]
    public void Hub_exposes_join_and_answer_completion_commands_for_text_workflow()
    {
        Assert.NotNull(typeof(InterviewUpdatesHub).GetMethod("JoinAttempt"));
        var completeAnswer = typeof(InterviewUpdatesHub).GetMethod("CompleteAnswer");
        Assert.NotNull(completeAnswer);
        Assert.Equal(new[] { "attemptId", "turnId", "answerText" },
            completeAnswer!.GetParameters().Select(parameter => parameter.Name));
    }
}
