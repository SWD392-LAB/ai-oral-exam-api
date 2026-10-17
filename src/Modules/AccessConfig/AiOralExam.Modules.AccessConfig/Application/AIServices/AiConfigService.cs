using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ValidationException = AiOralExam.SharedKernel.Errors.ValidationException;

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

public sealed record SaveAiServiceConfigRequest(
    AiServiceType ServiceType,
    [Required, StringLength(100)] string ProviderName,
    [Url, Required, StringLength(500)] string EndpointUrl,
    string? ApiKey,
    [Required] SpeechLanguage Language,
    bool IsActive = true);

public interface IAiConfigService
{
    Task<IReadOnlyList<AiServiceConfigDto>> ListAsync(CancellationToken ct);
    Task<AiServiceConfigDto> GetAsync(Guid id, CancellationToken ct);
    Task<AiServiceConfigDto> SaveAsync(Guid? id, SaveAiServiceConfigRequest r, CancellationToken ct);
    Task<AiServiceConfigDto> SaveActiveAsync(SaveAiServiceConfigRequest r, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
internal sealed class AiConfigService(AccessConfigDbContext db, ICurrentUser me, IDataProtectionProvider protection) : IAiConfigService
{
    IDataProtector P => protection.CreateProtector("AIVES.AiService.ApiKey.v1");
    static AiServiceConfigDto D(AiServiceConfig x)
        => new(x.Id, x.ServiceType.ToString(), x.ProviderName, x.EndpointUrl, x.Language.ToString(),
            x.IsActive, x.UpdatedAt, !string.IsNullOrEmpty(x.ApiKeyEncrypted));

    public async Task<IReadOnlyList<AiServiceConfigDto>> ListAsync(CancellationToken ct)
        => await db.AiServiceConfigs.AsNoTracking().OrderBy(x => x.ServiceType)
        .Select(x => new AiServiceConfigDto(
            x.Id, x.ServiceType.ToString(), x.ProviderName, x.EndpointUrl,
            x.Language.ToString(), x.IsActive, x.UpdatedAt, !string.IsNullOrEmpty(x.ApiKeyEncrypted)))
        .ToListAsync(ct);

    public async Task<AiServiceConfigDto> GetAsync(Guid id, CancellationToken ct)
        => D(await db.AiServiceConfigs.FindAsync([id], ct) ??
            throw new NotFoundException("ai_config_not_found", "AI service configuration not found."));

    public async Task<AiServiceConfigDto> SaveAsync(Guid? id, SaveAiServiceConfigRequest r, CancellationToken ct)
    {
        ValidateRequest(r);
        if (id is null && string.IsNullOrWhiteSpace(r.ApiKey))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(r.ApiKey)] = ["API key is required."]
            });
        AiServiceConfig x;
        if (id is null)
        {
            x = new AiServiceConfig
            {
                Id = Guid.NewGuid(),
                ConfiguredBy = me.Id
            };
            db.AiServiceConfigs.Add(x);
        }
        else
        {
            x = await db.AiServiceConfigs.FindAsync([id.Value], ct) ??
                throw new NotFoundException("ai_config_not_found", "AI service configuration not found.");
        }
        if (r.IsActive)
        {
            var active = await db.AiServiceConfigs.Where(x => x.ServiceType == r.ServiceType && x.IsActive && x.Id != (id ?? Guid.Empty))
                .ToListAsync(ct);
            foreach (var a in active)
                a.IsActive = false;
        }
        x.ServiceType = r.ServiceType;
        x.ProviderName = r.ProviderName.Trim();
        x.EndpointUrl = r.EndpointUrl.Trim();
        if (!string.IsNullOrWhiteSpace(r.ApiKey))
            x.ApiKeyEncrypted = P.Protect(r.ApiKey);
        x.Language = r.Language;
        x.IsActive = r.IsActive;
        x.UpdatedAt = DateTime.UtcNow;
        try
        {
            CourseService.AddAudit(db, me.Id, id is null ? AuditActions.Create : AuditActions.Update,
                AuditEntities.AiServiceConfig, x.Id, $"AI service {x.ServiceType} / {x.ProviderName}");
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_ai_config_active"
        })
        {
            throw new ConflictException("active_ai_config_exists",
                "An active AI service configuration already exists for this service type.");
        }
        return D(x);
    }

    public async Task<AiServiceConfigDto> SaveActiveAsync(SaveAiServiceConfigRequest r, CancellationToken ct)
    {
        ValidateRequest(r);
        var activeIds = await db.AiServiceConfigs
            .Where(x => x.ServiceType == r.ServiceType && x.IsActive)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var id = ExistingActiveConfigurationId(activeIds);
        return await SaveAsync(id, r with { IsActive = true }, ct);
    }

    internal static Guid? ExistingActiveConfigurationId(IReadOnlyCollection<Guid> activeIds)
    {
        if (activeIds.Count > 1)
            throw new ConflictException("multiple_active_ai_configs",
                "More than one active configuration exists for this service type.");
        return activeIds.Count == 0 ? null : activeIds.Single();
    }

    internal static void ValidateRequest(SaveAiServiceConfigRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(r.ServiceType))
            errors[nameof(r.ServiceType)] = ["A valid AI service type is required."];
        if (!Enum.IsDefined(r.Language))
            errors[nameof(r.Language)] = ["A valid speech language is required."];
        if (string.IsNullOrWhiteSpace(r.ProviderName) || r.ProviderName.Trim().Length > 100)
            errors[nameof(r.ProviderName)] = ["Provider name is required and must be no more than 100 characters."];
        var endpointUrl = r.EndpointUrl?.Trim();
        if (string.IsNullOrWhiteSpace(endpointUrl) || endpointUrl.Length > 500 ||
            !Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps) ||
            Uri.CheckHostName(endpointUri.Host) == UriHostNameType.Unknown)
            errors[nameof(r.EndpointUrl)] = ["Endpoint URL is required, must be no more than 500 characters, and must be an absolute HTTP or HTTPS URL with a valid host."];
        if (errors.Count > 0)
            throw new ValidationException(errors);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var x = await db.AiServiceConfigs.FindAsync([id], ct) ??
            throw new NotFoundException("ai_config_not_found", "AI service configuration not found.");
        var description = $"AI service {x.ServiceType} / {x.ProviderName}";
        db.AiServiceConfigs.Remove(x);
        CourseService.AddAudit(db, me.Id, AuditActions.Delete, AuditEntities.AiServiceConfig, id, description);
        await db.SaveChangesAsync(ct);
    }
}
