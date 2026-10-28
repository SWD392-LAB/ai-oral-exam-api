namespace AiOralExam.Modules.Interview.Application;

/// <summary>"Interview" section of appsettings.</summary>
public sealed class InterviewOptions
{
    public const string SectionName = "Interview";

    /// <summary>Extra seconds after the time limit (network delay) before an answer counts as timed out.</summary>
    public int TimeLimitGraceSeconds { get; set; } = 15;

    /// <summary>Allow another attempt after a finished one (open point - off by default).</summary>
    public bool AllowRetake { get; set; }

    /// <summary>Number of retries allowed after the initial turn-processing attempt.</summary>
    public int MaximumTurnRetries { get; set; } = 3;

    /// <summary>Initial delay before retrying failed turn processing.</summary>
    public int InitialTurnRetryDelaySeconds { get; set; } = 5;

    /// <summary>Upper bound for exponential turn-processing retry delays.</summary>
    public int MaximumTurnRetryDelaySeconds { get; set; } = 300;

    public MockOptions Mock { get; set; } = new();

    public sealed class MockOptions
    {
        /// <summary>M1 has no follow-ups -> false. Turn on to try the FOLLOW_UP flow with the mock.</summary>
        public bool EnableFollowUps { get; set; }
    }
}
