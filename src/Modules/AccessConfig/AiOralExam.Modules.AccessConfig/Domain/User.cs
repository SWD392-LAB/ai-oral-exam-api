namespace AiOralExam.Modules.AccessConfig.Domain;

/// <summary>ERD: USER. Sign-in is handled by Keycloak: Id = Keycloak user id (token "sub").</summary>
public class User
{
    public Guid Id { get; set; }
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string Email { get; set; } = null!;
    /// <summary>Unique sign-in name; equals the Keycloak username (users may also sign in with the email).</summary>
    public string Username { get; set; } = null!;
    public string? StudentCode { get; set; }
    public string? LecturerCode { get; set; }
    public string? ClassCode { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ICollection<CourseLecturer> CourseAssignments { get; set; } = new List<CourseLecturer>();
}
