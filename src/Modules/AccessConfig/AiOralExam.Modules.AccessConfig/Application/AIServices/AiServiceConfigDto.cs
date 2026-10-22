namespace AiOralExam.Modules.AccessConfig.Application.AIServices;

public sealed record AiServiceConfigDto(
    Guid Id,
    string ServiceType,
    string ProviderName,
    string EndpointUrl,
    string Language,
    bool IsActive,
    DateTime UpdatedAt,
    bool HasApiKey);
