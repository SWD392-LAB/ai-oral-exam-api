namespace AiOralExam.SharedKernel.Security;

/// <summary>Nguoi dung dang goi API (lay tu JWT). Implement o AiOralExam.Api.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>Id user; nem UnauthorizedException neu chua dang nhap.</summary>
    Guid Id { get; }

    string? Role { get; }

    bool IsInRole(string role);
}
