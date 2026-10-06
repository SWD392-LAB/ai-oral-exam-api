namespace AiOralExam.SharedKernel.Security;

/// <summary>Role names used in the JWT and in [Authorize(Roles = ...)]. Match the roles table.</summary>
public static class Roles
{
    public const string Student = "Student";
    public const string Lecturer = "Lecturer";
    public const string Administrator = "Administrator";

    public const string LecturerOrAdmin = Lecturer + "," + Administrator;
}

/// <summary>Claim names in the JWT (short names instead of Microsoft's long claim URIs).</summary>
public static class AppClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
}
