namespace AiOralExam.SharedKernel.Security;

/// <summary>JWT settings, read from the "Jwt" section of appsettings.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "aives-api";
    public string Audience { get; set; } = "aives-web";

    /// <summary>At least 32 characters. Real environments: set it through the Jwt__SigningKey environment variable.</summary>
    public string SigningKey { get; set; } = null!;
    public int ExpiresMinutes { get; set; } = 480;
}
