namespace AiOralExam.Modules.AccessConfig.Application.Keycloak;

public interface IKeycloakAdminClient
{
    Task<Guid> CreateUserAsync(string username, string email, string firstName, string lastName, CancellationToken ct = default);
    Task AssignRealmRoleAsync(Guid userId, string roleName, CancellationToken ct = default);
    Task<bool> SendSetPasswordEmailAsync(Guid userId, CancellationToken ct = default);
    Task DeleteUserAsync(Guid userId, CancellationToken ct = default);
}
