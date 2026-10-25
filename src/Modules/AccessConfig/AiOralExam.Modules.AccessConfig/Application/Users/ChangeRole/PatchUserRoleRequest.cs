using AiOralExam.Modules.AccessConfig.Domain;

namespace AiOralExam.Modules.AccessConfig.Application.Users.ChangeRole;

public sealed record PatchUserRoleRequest(UserRole Role, string? StudentCode = null, string? LecturerCode = null);
