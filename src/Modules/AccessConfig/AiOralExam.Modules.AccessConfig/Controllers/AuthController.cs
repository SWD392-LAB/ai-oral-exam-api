using AiOralExam.Modules.AccessConfig.Application.Auth;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

[ApiController]
[Route("api/auth")]
[Tags("Auth")]
public sealed class AuthController(IAuthService auth, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Dang nhap bang email + mat khau, tra ve JWT co role.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    public Task<LoginResponse> Login(LoginRequest request, CancellationToken ct) =>
        auth.LoginWithPasswordAsync(request, ct);

    /// <summary>Thong tin tai khoan dang dang nhap.</summary>
    [HttpGet("me")]
    [Authorize]
    public Task<UserProfileDto> Me(CancellationToken ct) =>
        auth.GetProfileAsync(currentUser.Id, ct);

    // TODO (M4): POST google, POST forgot-password, POST set-password
}
