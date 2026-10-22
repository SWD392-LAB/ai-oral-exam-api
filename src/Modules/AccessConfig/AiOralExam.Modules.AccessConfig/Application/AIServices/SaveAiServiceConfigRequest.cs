using System.ComponentModel.DataAnnotations;
using AiOralExam.Modules.AccessConfig.Domain;

namespace AiOralExam.Modules.AccessConfig.Application.AIServices;

public sealed record SaveAiServiceConfigRequest(
    AiServiceType ServiceType,
    [Required, StringLength(100)] string ProviderName,
    [Url, Required, StringLength(500)] string EndpointUrl,
    string? ApiKey,
    [Required] SpeechLanguage Language,
    bool IsActive = true);
