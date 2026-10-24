namespace AiOralExam.Modules.AccessConfig.Domain;

/// <summary>ERD: ROLE. Name mirrors the Keycloak realm role (Student, Lecturer, Administrator).</summary>
public class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
}
