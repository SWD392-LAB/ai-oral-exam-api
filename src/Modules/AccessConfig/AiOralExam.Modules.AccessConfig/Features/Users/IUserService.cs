using AiOralExam.Modules.AccessConfig.Domain;

namespace AiOralExam.Modules.AccessConfig.Application.Users;

public interface IUserService
{
    Task<IReadOnlyList<UserDto>> ListAsync(UserRole? role, bool? active, string? search, CancellationToken ct);
    Task<UserDto> GetAsync(Guid id, CancellationToken ct);
    Task<CreatedUserDto> CreateAsync(CreateUserRequest r, CancellationToken ct);
    Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest r, CancellationToken ct);
    Task ResendSetupEmailAsync(Guid id, Guid actorId, CancellationToken ct);
    Task UpdateOwnProfileAsync(Guid currentUserId, UpdateOwnProfileRequest request, CancellationToken ct);
}
