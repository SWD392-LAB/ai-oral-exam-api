namespace AiOralExam.Modules.Interview.Contracts;

/// <summary>
/// Reads the STORED results of attempts. Reporting uses this contract and
/// NEVER calls the AI again to re-score (business rule #5).
/// </summary>
public interface IInterviewResultsApi
{
    Task<AttemptResult?> GetAttemptResultAsync(Guid attemptId, CancellationToken ct = default);

    Task<IReadOnlyList<AttemptCounts>> GetAttemptCountsAsync(
        IReadOnlyCollection<Guid> sessionIds, CancellationToken ct = default);

    /// <summary>All finished attempts (PendingReview/Finalized) of an exam session.</summary>
    Task<IReadOnlyList<AttemptResult>> GetSessionResultsAsync(Guid examSessionId, CancellationToken ct = default);
}
