using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.AuditLogs;
internal sealed class AuditLogService(AccessConfigDbContext db) : IAuditLogService, IAuditLogWriter
{
    public async Task AppendAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = entry.UserId,
            Action = entry.Action,
            TargetEntity = entry.TargetEntity,
            TargetId = entry.TargetId,
            Description = entry.Description is null ? null : entry.Description[..Math.Min(entry.Description.Length, 500)],
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

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
            .Select(x => new AuditLogDto(x.l.Id, x.l.UserId, x.l.Action, x.l.TargetEntity, x.l.TargetId,
                x.l.CreatedAt, x.l.Description, x.u.FullName, x.u.Email))
            .ToListAsync(ct);
    }
}
