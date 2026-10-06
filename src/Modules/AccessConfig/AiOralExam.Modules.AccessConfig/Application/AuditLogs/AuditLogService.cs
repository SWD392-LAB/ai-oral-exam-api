using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.AuditLogs;
public sealed record AuditLogDto(
    Guid Id,
    Guid UserId,
    string Action,
    string TargetEntity,
    Guid? TargetId,
    DateTime CreatedAt,
    string UserName,
    string Email);

public interface IAuditLogService
{
    Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid? userId, string? action, string? targetEntity, DateTime? from, DateTime? to, int take, CancellationToken ct);
}
internal sealed class AuditLogService(AccessConfigDbContext db) : IAuditLogService
{
    public async Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid? userId, string? action, string? targetEntity, DateTime? from, DateTime? to, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 500);
        var q = from l in db.AuditLogs.AsNoTracking()
                join u in db.Users.AsNoTracking()
                on l.UserId equals u.Id
                select new { l, u };
        if (userId is not null)
            q = q.Where(x => x.l.UserId == userId);
        if (!string.IsNullOrWhiteSpace(action))
            q = q.Where(x => x.l.Action == action);
        if (!string.IsNullOrWhiteSpace(targetEntity))
            q = q.Where(x => x.l.TargetEntity == targetEntity);
        if (from is not null)
            q = q.Where(x => x.l.CreatedAt >= from);
        if (to is not null)
            q = q.Where(x => x.l.CreatedAt <= to);
        return await q.OrderByDescending(x => x.l.CreatedAt)
            .Take(take)
            .Select(x => new AuditLogDto(x.l.Id, x.l.UserId, x.l.Action, x.l.TargetEntity, x.l.TargetId, x.l.CreatedAt, x.u.FullName, x.u.Email))
            .ToListAsync(ct);
    }
}
