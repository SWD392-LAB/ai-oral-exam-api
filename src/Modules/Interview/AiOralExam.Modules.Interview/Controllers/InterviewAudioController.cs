using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Audio;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.Interview.Controllers;

[ApiController]
[Route("api/interview-attempts/{id:guid}")]
[Authorize]
[ApiExplorerSettings(GroupName = "v1")]
public sealed class InterviewAudioController(IInterviewAudioService audio) : ControllerBase
{
    [HttpPost("audio")]
    [Consumes("multipart/form-data")]
    [Authorize(Roles = Roles.Student)]
    [RequestSizeLimit(16 * 1024 * 1024)]
    [ProducesResponseType<SubmitAnswerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<SubmitAnswerResponse> Submit(
        Guid id,
        [FromForm] Guid turnId,
        IFormFile? file,
        CancellationToken ct)
    {
        if (file is null)
            throw new ValidationException(new Dictionary<string, string[]> { ["file"] = ["Audio file is required."] });
        await using var stream = file.OpenReadStream();
        return await audio.SubmitAudioAsync(id, turnId, stream, file.ContentType, file.Length, ct);
    }

    [HttpGet("turns/{turnId:guid}/question-audio")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> QuestionAudio(Guid id, Guid turnId, CancellationToken ct)
    {
        var result = await audio.GetQuestionAudioAsync(id, turnId, ct);
        return File(result.Content, result.ContentType);
    }

    [HttpGet("turns/{turnId:guid}/answer-audio")]
    [Authorize(Roles = Roles.StudentOrLecturerOrAdmin)]
    public async Task<IActionResult> AnswerAudio(Guid id, Guid turnId, CancellationToken ct)
    {
        var result = await audio.GetAnswerAudioAsync(id, turnId, ct);
        return File(result.Content, result.ContentType);
    }
}
