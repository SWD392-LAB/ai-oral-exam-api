using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.AIServices;

public sealed record AiServiceConfigDto(
    Guid Id,
    string ServiceType,
    string ProviderName,
    string EndpointUrl,
    string Language,
    bool IsActive,
    DateTime UpdatedAt);

public sealed record SaveAiServiceConfigRequest(
    AiServiceType ServiceType,
    [property: Required] string ProviderName,
    [property: Url, Required] string EndpointUrl,
    [property: Required] string ApiKey,
    [property: Required] SpeechLanguage Language,
    bool IsActive = true);

public interface IAiConfigService
{
    Task<IReadOnlyList<AiServiceConfigDto>> ListAsync(CancellationToken ct);
    Task<AiServiceConfigDto> GetAsync(Guid id, CancellationToken ct);
    Task<AiServiceConfigDto> SaveAsync(Guid? id, SaveAiServiceConfigRequest r, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
internal sealed class AiConfigService(AccessConfigDbContext db, ICurrentUser me, IDataProtectionProvider protection) : IAiConfigService
{
    IDataProtector P => protection.CreateProtector("AIVES.AiService.ApiKey.v1");
    static AiServiceConfigDto D(AiServiceConfig x)
        => new(x.Id, x.ServiceType.ToString(), x.ProviderName, x.EndpointUrl, x.Language.ToString(), x.IsActive, x.UpdatedAt);

    public async Task<IReadOnlyList<AiServiceConfigDto>> ListAsync(CancellationToken ct)
        => await db.AiServiceConfigs.AsNoTracking().OrderBy(x => x.ServiceType)
        .Select(x => new AiServiceConfigDto(
            x.Id, x.ServiceType.ToString(), x.ProviderName, x.EndpointUrl,
            x.Language.ToString(), x.IsActive, x.UpdatedAt))
        .ToListAsync(ct);

    public async Task<AiServiceConfigDto> GetAsync(Guid id, CancellationToken ct)
        => D(await db.AiServiceConfigs.FindAsync([id], ct) ??
            throw new NotFoundException("ai_config_not_found", "AI service configuration not found."));

    public async Task<AiServiceConfigDto> SaveAsync(Guid? id, SaveAiServiceConfigRequest r, CancellationToken ct)
    {
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
        x.ApiKeyEncrypted = P.Protect(r.ApiKey);
        x.Language = r.Language;
        x.IsActive = r.IsActive;
        x.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, id is null ? "Create" : "Update", "AiServiceConfig", x.Id, ct);
        return D(x);
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var x = await db.AiServiceConfigs.FindAsync([id], ct) ??
            throw new NotFoundException("ai_config_not_found", "AI service configuration not found.");
        db.AiServiceConfigs.Remove(x);
        await db.SaveChangesAsync(ct);
        await CourseService.Audit(db, me.Id, "Delete", "AiServiceConfig", id, ct);
    }
}
