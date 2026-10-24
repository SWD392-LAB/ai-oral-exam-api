namespace AiOralExam.SharedKernel.Security;

/// <summary>Claim names in the JWT (short names instead of Microsoft's long claim URIs).</summary>
public static class AppClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
}
