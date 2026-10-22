using AiOralExam.Modules.AccessConfig.Domain;

namespace AiOralExam.Modules.AccessConfig.Controllers;

public sealed record PatchUserRoleRequest(UserRole Role, string? StudentCode = null, string? LecturerCode = null);
