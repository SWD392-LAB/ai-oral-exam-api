using System.Collections.Generic;
using System.Linq;
using AiOralExam.Modules.AccessConfig.Application.Users;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Pagination;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.Users.ListUsers;

public sealed class ListUsersService(AccessConfigDbContext db)
{
    public async Task<PagedResult<UserDto>> ListAsync(
        UserRole? role,
        bool? active,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var offset = ValidatePagination(page, pageSize);

        IQueryable<User> q = db.Users
            .AsNoTracking()
            .Include(x => x.Role);

        if (role is not null)
            q = q.Where(x => x.Role.Name == role.ToString());

        if (active is not null)
            q = q.Where(x => x.IsActive == active);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var p = $"%{search.Trim()}%";
            q = q.Where(x =>
                EF.Functions.ILike(x.FullName, p) ||
                EF.Functions.ILike(x.Email, p) ||
                EF.Functions.ILike(x.StudentCode ?? "", p) ||
                EF.Functions.ILike(x.LecturerCode ?? "", p));
        }

        var totalCount = await q.CountAsync(ct);

        var rows = await q
            .OrderBy(x => x.FullName)
            .ThenBy(x => x.Id)
            .Skip(offset)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(x => new UserDto(
            x.Id,
            x.FullName,
            x.Email,
            x.Username,
            x.Role.Name,
            x.StudentCode,
            x.LecturerCode,
            x.IsActive)).ToList();

        return new PagedResult<UserDto>(
            items, page, pageSize, totalCount);
    }

    private static int ValidatePagination(int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
            errors[nameof(page)] = ["Page must be at least 1."];
        if (pageSize < 1 || pageSize > 100)
            errors[nameof(pageSize)] = ["Page size must be between 1 and 100."];

        var offset = (long)(page - 1) * pageSize;
        if (errors.Count == 0 && offset > int.MaxValue)
            errors[nameof(page)] = ["Page is too large for the requested page size."];

        if (errors.Count > 0)
            throw new ValidationException(errors, "Invalid pagination parameters.");

        return (int)offset;
    }
}
