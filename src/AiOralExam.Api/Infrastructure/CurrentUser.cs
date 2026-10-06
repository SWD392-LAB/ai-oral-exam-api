using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;

namespace AiOralExam.Api.Infrastructure;

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid Id =>
        Guid.TryParse(Principal?.FindFirst(AppClaims.UserId)?.Value, out var id)
            ? id
            : throw new UnauthorizedException("unauthenticated", "Bạn cần đăng nhập.");

    public string? Role => Principal?.FindFirst(AppClaims.Role)?.Value;

    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;
}
