namespace AiOralExam.Modules.AccessConfig.Domain;

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
