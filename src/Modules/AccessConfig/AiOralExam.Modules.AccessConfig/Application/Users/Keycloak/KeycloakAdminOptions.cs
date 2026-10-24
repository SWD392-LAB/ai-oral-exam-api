namespace AiOralExam.Modules.AccessConfig.Application.Users.Keycloak;

public sealed class KeycloakAdminOptions
{
    public const string SectionName = "KeycloakAdmin";

    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string Realm { get; set; } = "aives";
    public string ClientId { get; set; } = "aives-admin-api";
    public string ClientSecret { get; set; } = null!;
}
