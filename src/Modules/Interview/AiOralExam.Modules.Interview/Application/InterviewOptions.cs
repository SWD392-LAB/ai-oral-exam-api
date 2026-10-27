namespace AiOralExam.Modules.Interview.Application;

/// <summary>"Interview" section of appsettings.</summary>
public sealed class InterviewOptions
{
    public const string SectionName = "Interview";

    /// <summary>Extra seconds after the time limit (network delay) before an answer counts as timed out.</summary>
    public int TimeLimitGraceSeconds { get; set; } = 15;

    /// <summary>Allow another attempt after a finished one (open point - off by default).</summary>
    public bool AllowRetake { get; set; }

    public MockOptions Mock { get; set; } = new();

    public sealed class MockOptions
    {
        /// <summary>M1 has no follow-ups -> false. Turn on to try the FOLLOW_UP flow with the mock.</summary>
        public bool EnableFollowUps { get; set; }
    }
}
