using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.Interview.Controllers;

[ApiController]
[Route("api/interview-attempts/{id:guid}")]
[Authorize(Roles = Roles.Lecturer)]
public sealed class InterviewAttemptReviewsController(IInterviewService interview) : ControllerBase
{
    [HttpGet("review")]
    public Task<AttemptResult> GetReview(Guid id, CancellationToken ct) =>
        interview.GetAttemptReviewAsync(id, ct);

    [HttpPut("score-review")]
    public Task<ScoreReviewResult> SubmitScoreReview(Guid id, SubmitScoreReviewRequest request, CancellationToken ct) =>
        interview.SubmitScoreReviewAsync(id, request, ct);
}
