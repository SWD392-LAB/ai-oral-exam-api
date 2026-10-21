using System.Collections.Generic;
using System.Linq;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.Users;

internal sealed class ListUsersService(AccessConfigDbContext db)
{
    public async Task<IReadOnlyList<UserDto>> ListAsync(
        UserRole? role, bool? active, string? search, CancellationToken ct)
    {
        IQueryable<User> q = db.Users.AsNoTracking().Include(x => x.Role);
        if (role is not null)
            q = q.Where(x => x.Role.Name == role.ToString());
        if (active is not null)
            q = q.Where(x => x.IsActive == active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var p = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.FullName, p) ||
            EF.Functions.ILike(x.Email, p) ||
            EF.Functions.ILike(x.StudentCode ?? "", p) ||
            EF.Functions.ILike(x.LecturerCode ?? "", p));
        }
        var rows = await q.OrderBy(x => x.FullName).ToListAsync(ct);
        return rows.Select(x => new UserDto(
            x.Id,
            x.FullName,
            x.Email,
            x.Username,
            x.Role.Name,
            x.StudentCode,
            x.LecturerCode,
            x.IsActive)).ToList();
    }
}
