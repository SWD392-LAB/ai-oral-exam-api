using AiOralExam.Modules.Interview.Application;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.Interview.Controllers;

/// <summary>API luong thi cua sinh vien - chi role Student (BE-PLAT-07).</summary>
[ApiController]
[Route("api")]
[Tags("Interview")]
[Authorize(Roles = Roles.Student)]
public sealed class InterviewAttemptsController(IInterviewService interview) : ControllerBase
{
    /// <summary>Phien thi sinh vien duoc dang ky (khong gom phien Draft).</summary>
    [HttpGet("interview/my-sessions")]
    public Task<IReadOnlyList<MySessionDto>> MySessions(CancellationToken ct) =>
        interview.GetMySessionsAsync(ct);

    /// <summary>
    /// Tao luot thi va tra ve cau hoi dau tien. Neu dang co luot thi InProgress thi tra lai luot do (200).
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

    /// <summary>Trang thai hien tai cua luot thi (dung khi FE reload trang).</summary>
    [HttpGet("interview-attempts/{id:guid}")]
    public Task<AttemptStateDto> Get(Guid id, CancellationToken ct) =>
        interview.GetAttemptStateAsync(id, ct);

    /// <summary>
    /// Nop cau tra loi (M1: dang text) cho cau hoi dang hoi. Tra ve cau hoi xoay, cau tiep theo
    /// hoac ket thuc, kem diem goi y cua AI (neu InterviewOptions.ShowAiScoreToStudent).
    /// </summary>
    [HttpPost("interview-attempts/{id:guid}/responses")]
    [ProducesResponseType<SubmitAnswerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public Task<SubmitAnswerResponse> Submit(Guid id, SubmitAnswerRequest request, CancellationToken ct) =>
        interview.SubmitAnswerAsync(id, request, ct);

    // TODO (M2): POST interview-attempts/{id}/audio (multipart) -> STT -> SubmitAnswer; WebSocket /hubs/interview
    // TODO (M3): API giang vien xem lai transcript + chot diem (ScoreReview) -> attempt Finalized
}
