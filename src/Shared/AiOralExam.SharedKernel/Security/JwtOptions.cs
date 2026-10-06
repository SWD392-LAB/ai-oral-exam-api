namespace AiOralExam.SharedKernel.Security;

/// <summary>Cau hinh JWT, doc tu section "Jwt" trong appsettings.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "aives-api";
    public string Audience { get; set; } = "aives-web";

    /// <summary>Toi thieu 32 ky tu. Moi truong that: dat qua bien moi truong Jwt__SigningKey.</summary>
    public string SigningKey { get; set; } = null!;
    public int ExpiresMinutes { get; set; } = 480;
}
