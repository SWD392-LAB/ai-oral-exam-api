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

/// <summary>ERD: ROLE. Name mirrors the Keycloak realm role (Student, Lecturer, Administrator).</summary>
public class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
}

/// <summary>ERD: AUDIT LOG.</summary>
public class AuditLog
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Action { get; set; } = null!;
    public string TargetEntity { get; set; } = null!;
    public Guid? TargetId { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>ERD: AI SERVICE CONFIG. Only one IsActive row per ServiceType.</summary>
public class AiServiceConfig
{
    public Guid Id { get; set; }
    public Guid ConfiguredBy { get; set; }
    public AiServiceType ServiceType { get; set; }
    public string ProviderName { get; set; } = null!;
    public string EndpointUrl { get; set; } = null!;
    public string ApiKeyEncrypted { get; set; } = null!;
    public SpeechLanguage Language { get; set; } = SpeechLanguage.Vietnamese;
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}
