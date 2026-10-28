using AiOralExam.Modules.Interview.Contracts;

namespace AiOralExam.Modules.Interview.Domain;

/// <summary>ERD: INTERVIEW TURN - one question/answer exchange. The turns together are the transcript.</summary>
public class InterviewTurn
{
    public Guid Id { get; set; }
    public Guid QuestionResponseId { get; set; }
    public int TurnNo { get; set; }
    public TurnType Type { get; set; }
    public string QuestionText { get; set; } = null!;
    public string? AnswerTranscript { get; set; }
    public string? AudioUrl { get; set; }
    public DateTime AskedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public bool IsTimedOut { get; set; }
    public TurnProcessingStatus ProcessingStatus { get; set; } = TurnProcessingStatus.AwaitingAnswer;
    public DateTime? ProcessingStartedAt { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public Guid? ProcessingToken { get; set; }
    public string? SubmissionResultJson { get; set; }
}

public enum TurnProcessingStatus
{
    AwaitingAnswer,
    Processing,
    Retry,
    Completed,
    Failed
}
