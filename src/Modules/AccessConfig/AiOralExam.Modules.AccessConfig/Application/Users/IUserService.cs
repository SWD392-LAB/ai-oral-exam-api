using AiOralExam.Modules.AccessConfig.Application.Users.UpdateOwnProfile;
using AiOralExam.Modules.AccessConfig.Domain;

namespace AiOralExam.Modules.AccessConfig.Application.Users;

public interface IUserService
{
    Task ResendSetupEmailAsync(Guid id, Guid actorId, CancellationToken ct);
    Task UpdateOwnProfileAsync(Guid currentUserId, UpdateOwnProfileRequest request, CancellationToken ct);
}
