using AiOralExam.Modules.AccessConfig.Application.Auth;
using AiOralExam.Modules.AccessConfig.Application.Users;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;

/// <summary>Sign-in happens in Keycloak (realm aives); clients send its access token as a Bearer token.</summary>
[ApiController]
[Route("api/auth")]
[Tags("Auth")]
public sealed class AuthController(IAuthService auth, IUserService users, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Profile of the signed-in account.</summary>
    [HttpGet("me")]
    [Authorize]
    public Task<UserProfileDto> Me(CancellationToken ct) =>
        auth.GetProfileAsync(currentUser.Id, ct);

    [HttpPatch("me")]
    [Authorize]
    public async Task<UserProfileDto> UpdateMe(UpdateOwnProfileRequest request, CancellationToken ct)
    {
        var userId = currentUser.Id;
        await users.UpdateOwnProfileAsync(userId, request, ct);
        return await auth.GetProfileAsync(userId, ct);
    }
}
