using AiOralExam.Modules.Interview.Application;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.Interview.Controllers;

/// <summary>Student exam-flow API - Student role only (BE-PLAT-07).</summary>
[ApiController]
[Route("api")]
[Tags("Interview")]
[Authorize(Roles = Roles.Student)]
public sealed class InterviewAttemptsController(IInterviewService interview) : ControllerBase
{
    /// <summary>Exam sessions the student is registered for (Draft sessions excluded).</summary>
    [HttpGet("interview/my-sessions")]
    public Task<IReadOnlyList<MySessionDto>> MySessions(CancellationToken ct) =>
        interview.GetMySessionsAsync(ct);

    /// <summary>
    /// Creates an attempt and returns the first question. If an InProgress attempt exists, returns it (200).
    /// </summary>
    [HttpPost("interview-attempts")]
    [ProducesResponseType<AttemptStateDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<AttemptStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Start(StartAttemptRequest request, CancellationToken ct)
    {
        var (state, created) = await interview.StartAttemptAsync(request, ct);
        return created
            ? CreatedAtAction(nameof(Get), new { id = state.AttemptId }, state)
            : Ok(state);
    }

    /// <summary>Current state of the attempt (used when the FE reloads the page).</summary>
    [HttpGet("interview-attempts/{id:guid}")]
    public Task<AttemptStateDto> Get(Guid id, CancellationToken ct) =>
        interview.GetAttemptStateAsync(id, ct);

    /// <summary>
    /// Submits the answer (M1: text) to the current question. Returns a follow-up, the next question
    /// or completion, plus the AI-suggested score (if InterviewOptions.ShowAiScoreToStudent).
    /// </summary>
    [HttpPost("interview-attempts/{id:guid}/responses")]
    [ProducesResponseType<SubmitAnswerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public Task<SubmitAnswerResponse> Submit(Guid id, SubmitAnswerRequest request, CancellationToken ct) =>
        interview.SubmitAnswerAsync(id, request, ct);

    // TODO (M2): POST interview-attempts/{id}/audio (multipart) -> STT -> SubmitAnswer; WebSocket /hubs/interview
    // TODO (M3): lecturer API to review the transcript + confirm the score (ScoreReview) -> attempt Finalized
}
