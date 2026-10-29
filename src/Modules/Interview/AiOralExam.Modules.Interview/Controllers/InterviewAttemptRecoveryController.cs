using AiOralExam.Modules.Interview.Application;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.Interview.Controllers;

/// <summary>
/// Temporary synchronous recovery API. Audit persistence is added in Round 3 Phase 2 STEP 4;
/// until then this endpoint must not be treated as production-ready.
/// </summary>
[ApiController]
[Route("api/interview-attempts/{id:guid}/recovery")]
[Authorize(Roles = Roles.LecturerOrAdmin)]
public sealed class InterviewAttemptRecoveryController(IInterviewService interview) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<RecoverInterruptedTurnResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiError>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<RecoverInterruptedTurnResponse> Recover(
        Guid id, RecoverInterruptedTurnRequest request, CancellationToken ct)
    {
        if (request.TurnId == Guid.Empty)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(request.TurnId)] = ["turnId is required and must be a non-empty GUID."]
            });

        var result = await interview.RecoverInterruptedTurnAsync(id, request.TurnId, ct);
        return new RecoverInterruptedTurnResponse(result);
    }
}
