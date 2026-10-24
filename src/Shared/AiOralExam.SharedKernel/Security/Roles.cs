namespace AiOralExam.SharedKernel.Security;

/// <summary>Role names used in the JWT and in [Authorize(Roles = ...)]. Match the roles table.</summary>
public static class Roles
{
    public const string Student = "Student";
    public const string Lecturer = "Lecturer";
    public const string Administrator = "Administrator";

    public const string LecturerOrAdmin = Lecturer + "," + Administrator;
}
