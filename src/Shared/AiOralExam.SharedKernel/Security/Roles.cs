namespace AiOralExam.SharedKernel.Security;

/// <summary>Ten role dung trong JWT va [Authorize(Roles = ...)]. Khop voi bang roles.</summary>
public static class Roles
{
    public const string Student = "Student";
    public const string Lecturer = "Lecturer";
    public const string Administrator = "Administrator";

    public const string LecturerOrAdmin = Lecturer + "," + Administrator;
}

/// <summary>Ten claim trong JWT (khong dung claim URI dai cua Microsoft).</summary>
public static class AppClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
}
