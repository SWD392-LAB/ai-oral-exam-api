namespace AiOralExam.SharedKernel.Security;

/// <summary>The user calling the API (read from the JWT). Implemented in AiOralExam.Api.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>User id; throws UnauthorizedException when not signed in.</summary>
    Guid Id { get; }

    string? Role { get; }

    bool IsInRole(string role);
}
