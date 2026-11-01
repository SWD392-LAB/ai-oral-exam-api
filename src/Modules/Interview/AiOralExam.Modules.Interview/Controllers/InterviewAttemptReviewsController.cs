using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Features.ScoreReview;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.Interview.Controllers;

[ApiController]
[Route("api/interview-attempts/{id:guid}")]
[Authorize(Roles = Roles.Lecturer)]
public sealed class InterviewAttemptReviewsController(IInterviewReviewService interview) : ControllerBase
{
    [HttpGet("/api/exam-sessions/{sessionId:guid}/score-reviews")]
    public Task<IReadOnlyList<AttemptResult>> GetSessionReviews(Guid sessionId, CancellationToken ct) =>
        interview.GetSessionAttemptReviewsAsync(sessionId, ct);

    [HttpGet("review")]
    public Task<AttemptResult> GetReview(Guid id, CancellationToken ct) =>
        interview.GetAttemptReviewAsync(id, ct);

    [HttpPut("score-review/draft")]
    public Task<ScoreReviewResult> SaveDraft(Guid id, SubmitScoreReviewRequest request, CancellationToken ct) =>
        interview.SaveScoreReviewDraftAsync(id, request, ct);

    [HttpPost("score-review/confirm")]
    public Task<ScoreReviewResult> Confirm(Guid id, CancellationToken ct) =>
        interview.ConfirmScoreReviewAsync(id, ct);

    [HttpPut("score-review")]
    public Task<ScoreReviewResult> SubmitScoreReview(Guid id, SubmitScoreReviewRequest request, CancellationToken ct) =>
        interview.SubmitScoreReviewAsync(id, request, ct);
}
