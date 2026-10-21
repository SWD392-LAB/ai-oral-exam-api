using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.Users;

internal sealed class GetUserService(AccessConfigDbContext db)
{
    public async Task<UserDto> GetAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users.Include(x => x.Role)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ??
            throw new NotFoundException("user_not_found", "User not found.");

        return new UserDto(
            user.Id,
            user.FullName,
            user.Email,
            user.Username,
            user.Role.Name,
            user.StudentCode,
            user.LecturerCode,
            user.IsActive);
    }
}
