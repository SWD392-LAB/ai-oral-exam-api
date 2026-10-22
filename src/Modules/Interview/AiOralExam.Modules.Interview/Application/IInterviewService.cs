namespace AiOralExam.Modules.Interview.Application;

public interface IInterviewService
{
    Task<IReadOnlyList<MySessionDto>> GetMySessionsAsync(CancellationToken ct = default);

    /// <summary>POST /api/interview-attempts (BE-PLAT-04). Returns (state, created).</summary>
    Task<(AttemptStateDto State, bool Created)> StartAttemptAsync(StartAttemptRequest request, CancellationToken ct = default);

    /// <summary>POST /api/interview-attempts/{id}/responses (BE-AI-05).</summary>
    Task<SubmitAnswerResponse> SubmitAnswerAsync(Guid attemptId, SubmitAnswerRequest request, CancellationToken ct = default);

    Task<AttemptStateDto> GetAttemptStateAsync(Guid attemptId, CancellationToken ct = default);
}
