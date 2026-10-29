using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Errors;

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
    public DateTime? AudioCompletionReceivedAt { get; set; }
    public DateTime? AudioAcceptedAt { get; set; }
    public bool IsTimedOut { get; set; }
    public TurnProcessingStatus ProcessingStatus { get; set; } = TurnProcessingStatus.AwaitingAnswer;
    public DateTime? ProcessingStartedAt { get; set; }
    public int RetryCount { get; set; }
    public int TranscriptionRetryCount { get; set; }
    public int ManualRecoveryCount { get; private set; }
    public DateTime? NextRetryAt { get; set; }
    public Guid? ProcessingToken { get; set; }
    public string? SubmissionResultJson { get; set; }

    public TurnProcessingStage ProcessingStage => IsTimedOut
    ? TurnProcessingStage.TimeoutEvaluation
    : AudioAcceptedAt is not null && AnsweredAt is null
        ? TurnProcessingStage.Transcription
        : AnsweredAt is not null
            ? TurnProcessingStage.AiEvaluation
            : AudioCompletionReceivedAt is not null
                ? TurnProcessingStage.AudioValidation
                : TurnProcessingStage.AwaitingAnswer;

    public void PrepareManualRecovery(AttemptStatus attemptStatus)
    {
        if (attemptStatus != AttemptStatus.Interrupted)
            throw new ConflictException("attempt_not_interrupted", "Only interrupted attempts can be manually recovered.");
        if (ProcessingStatus != TurnProcessingStatus.Failed)
            throw new ConflictException("turn_not_failed", "Only failed turns can be manually recovered.");
        if (ManualRecoveryCount >= 1)
            throw new ConflictException("manual_recovery_already_used", "This turn has already used its manual recovery.");

        if (ProcessingStage == TurnProcessingStage.Transcription)
            TranscriptionRetryCount = 0;
        else
            RetryCount = 0;

        ManualRecoveryCount++;
        ProcessingStatus = TurnProcessingStatus.Retry;
        ProcessingStartedAt = null;
        ProcessingToken = null;
        NextRetryAt = null;
    }
}

public enum TurnProcessingStage
{
    AwaitingAnswer,
    AudioValidation,
    Transcription,
    AiEvaluation,
    TimeoutEvaluation
}

public enum TurnProcessingStatus
{
    AwaitingAnswer,
    Processing,
    Retry,
    Completed,
    Failed
}
