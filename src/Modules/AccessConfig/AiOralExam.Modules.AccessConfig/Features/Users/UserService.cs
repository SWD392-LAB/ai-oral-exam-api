using System;
using System.Collections.Generic;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Application.Keycloak;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using ValidationException = AiOralExam.SharedKernel.Errors.ValidationException;

namespace AiOralExam.Modules.AccessConfig.Application.Users;
internal sealed class UserService(
    AccessConfigDbContext db,
    IKeycloakAdminClient keycloak) : IUserService
{
    public async Task ResendSetupEmailAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ??
            throw new NotFoundException("user_not_found", "User not found.");
        if (!user.IsActive)
            throw new ConflictException("user_inactive", "Cannot send a setup email to an inactive user.");
        if (!await keycloak.SendSetPasswordEmailAsync(user.Id, ct))
            throw new AppException(502, "setup_email_failed", "Keycloak could not send the password setup email.");

        CourseService.AddAudit(db, actorId, AuditActions.ResendSetupEmail, AuditEntities.User, id,
            $"User {user.Username}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Updates the display name in the application database only; the Keycloak profile is not synchronized.</summary>
    public async Task UpdateOwnProfileAsync(
        Guid currentUserId, UpdateOwnProfileRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == currentUserId, ct) ??
            throw new NotFoundException("user_not_found", "Account not found.");
        if (string.IsNullOrWhiteSpace(request.FullName))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["fullName"] = ["A full name is required."]
            });

        var fullName = request.FullName.Trim();
        if (fullName.Length > 150)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["fullName"] = ["The full name must not exceed 150 characters."]
            });

        user.FullName = fullName;
        CourseService.AddAudit(db, currentUserId, AuditActions.UpdateOwnProfile, AuditEntities.User, user.Id,
            $"User {user.Username}");
        await db.SaveChangesAsync(ct);
    }

}
