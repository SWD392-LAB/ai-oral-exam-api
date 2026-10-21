using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Keycloak;
using AiOralExam.Modules.AccessConfig.Application.Users;
using AiOralExam.Modules.AccessConfig.Controllers;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiOralExam.UnitTests;

public class UserUpdateTests
{
    [Fact]
    public void Administrator_update_can_change_role_with_matching_codes()
    {
        var user = ExistingUser(UserRole.Student, "S001", null);
        var request = new UpdateUserRequest(" Lecturer ", null, "L001", true, UserRole.Lecturer);

        var values = UserService.PrepareUpdate(user, request);
        UserService.ApplyUpdate(user, values, RoleFor(values.Role));

        Assert.Equal(UserRole.Lecturer.ToString(), user.Role.Name);
        Assert.Null(user.StudentCode);
        Assert.Equal("L001", user.LecturerCode);
        Assert.Equal("Lecturer", user.FullName);
    }

    [Fact]
    public void Student_to_lecturer_change_rejects_a_retained_student_code()
    {
        var user = ExistingUser(UserRole.Student, "S001", null);
        var request = new UpdateUserRequest("User", "S001", "L001", true, UserRole.Lecturer);

        Assert.Throws<ValidationException>(() => UserService.PrepareUpdate(user, request));
    }

    [Fact]
    public void Lecturer_to_student_change_rejects_a_retained_lecturer_code()
    {
        var user = ExistingUser(UserRole.Lecturer, null, "L001");
        var request = new UpdateUserRequest("User", "S001", "L001", true, UserRole.Student);

        Assert.Throws<ValidationException>(() => UserService.PrepareUpdate(user, request));
    }

    [Theory]
    [InlineData(UserRole.Student, null, null)]
    [InlineData(UserRole.Lecturer, null, null)]
    [InlineData(UserRole.Administrator, "S001", null)]
    [InlineData(UserRole.Administrator, null, "L001")]
    public void Invalid_role_code_combinations_are_rejected(
        UserRole role, string? studentCode, string? lecturerCode)
    {
        var user = ExistingUser(UserRole.Administrator, null, null);
        var request = new UpdateUserRequest("User", studentCode, lecturerCode, true, role);

        Assert.Throws<ValidationException>(() => UserService.PrepareUpdate(user, request));
    }

    [Fact]
    public void Update_without_role_preserves_existing_role_and_other_update_behavior()
    {
        var user = ExistingUser(UserRole.Student, "S001", null);
        var request = new UpdateUserRequest("  Updated User  ", " S002 ", null, false);

        Assert.Null(UpdateConflictCode(user, Guid.NewGuid(), request));

        var values = UserService.PrepareUpdate(user, request);
        UserService.ApplyUpdate(user, values, RoleFor(values.Role));

        Assert.Equal(UserRole.Student.ToString(), user.Role.Name);
        Assert.Equal("Updated User", user.FullName);
        Assert.Equal("S002", user.StudentCode);
        Assert.Null(user.LecturerCode);
        Assert.False(user.IsActive);
    }

    [Fact]
    public void Administrator_cannot_change_their_own_role()
    {
        var user = ExistingUser(UserRole.Administrator, null, null);
        var request = new UpdateUserRequest("Admin", null, "L001", true, UserRole.Lecturer);

        Assert.Equal("cannot_modify_self", UpdateConflictCode(user, user.Id, request));
    }

    [Fact]
    public void Administrator_cannot_deactivate_themselves()
    {
        var user = ExistingUser(UserRole.Administrator, null, null);
        var request = new UpdateUserRequest("Admin", null, null, false);

        Assert.Equal("cannot_modify_self", UpdateConflictCode(user, user.Id, request));
    }

    [Fact]
    public void Last_active_administrator_cannot_be_demoted()
    {
        var user = ExistingUser(UserRole.Administrator, null, null);
        var request = new UpdateUserRequest("Admin", null, "L001", true, UserRole.Lecturer);

        Assert.Equal("last_administrator", UpdateConflictCode(user, Guid.NewGuid(), request));
    }

    [Fact]
    public void Last_active_administrator_cannot_be_deactivated()
    {
        var user = ExistingUser(UserRole.Administrator, null, null);
        var request = new UpdateUserRequest("Admin", null, null, false);

        Assert.Equal("last_administrator", UpdateConflictCode(user, Guid.NewGuid(), request));
    }

    [Fact]
    public void Administrator_can_be_demoted_when_another_active_administrator_exists()
    {
        var user = ExistingUser(UserRole.Administrator, null, null);
        var otherAdmin = ExistingUser(UserRole.Administrator, null, null);
        var request = new UpdateUserRequest("Admin", null, "L001", true, UserRole.Lecturer);

        Assert.Null(UpdateConflictCode(user, Guid.NewGuid(), request, otherAdmin));
    }

    [Fact]
    public void Inactive_administrator_does_not_count_as_another_administrator()
    {
        var user = ExistingUser(UserRole.Administrator, null, null);
        var inactiveAdmin = ExistingUser(UserRole.Administrator, null, null);
        inactiveAdmin.IsActive = false;
        var request = new UpdateUserRequest("Admin", null, "L001", true, UserRole.Lecturer);

        Assert.Equal("last_administrator", UpdateConflictCode(user, Guid.NewGuid(), request, inactiveAdmin));
    }

    [Fact]
    public void Deactivation_marks_user_inactive_and_preserves_the_user_identity()
    {
        var user = ExistingUser(UserRole.Student, "S001", null);
        var userId = user.Id;
        var email = user.Email;
        var request = new UpdateUserRequest("Existing User", "S001", null, false);

        var values = UserService.PrepareUpdate(user, request);
        UserService.ApplyUpdate(user, values, user.Role);

        Assert.False(user.IsActive);
        Assert.Equal(userId, user.Id);
        Assert.Equal(email, user.Email);
    }

    [Fact]
    public async Task Role_change_writes_only_a_change_role_entry()
    {
        var audits = await UpdateAndGetAuditLogsAsync(
            UserRole.Student, "S001", null, true,
            new UpdateUserRequest("Existing User", null, "L001", true, UserRole.Lecturer));

        var audit = Assert.Single(audits);
        Assert.Equal(AuditActions.ChangeRole, audit.Action);
        Assert.Equal("User user: role Student -> Lecturer", audit.Description);
    }

    [Fact]
    public async Task Deactivation_writes_a_deactivate_entry()
    {
        var audits = await UpdateAndGetAuditLogsAsync(
            UserRole.Student, "S001", null, true,
            new UpdateUserRequest("Existing User", "S001", null, false));

        var audit = Assert.Single(audits);
        Assert.Equal(AuditActions.Deactivate, audit.Action);
        Assert.Equal("User user: deactivated", audit.Description);
    }

    [Fact]
    public async Task Reactivation_writes_a_reactivate_entry()
    {
        var audits = await UpdateAndGetAuditLogsAsync(
            UserRole.Student, "S001", null, false,
            new UpdateUserRequest("Existing User", "S001", null, true));

        var audit = Assert.Single(audits);
        Assert.Equal(AuditActions.Reactivate, audit.Action);
        Assert.Equal("User user: reactivated", audit.Description);
    }

    [Fact]
    public async Task Name_change_writes_an_update_entry()
    {
        var audits = await UpdateAndGetAuditLogsAsync(
            UserRole.Student, "S001", null, true,
            new UpdateUserRequest("Updated User", "S001", null, true));

        var audit = Assert.Single(audits);
        Assert.Equal(AuditActions.Update, audit.Action);
        Assert.Equal("User user (Student)", audit.Description);
    }

    [Fact]
    public async Task Role_change_with_name_change_writes_change_role_and_update_entries()
    {
        var audits = await UpdateAndGetAuditLogsAsync(
            UserRole.Student, "S001", null, true,
            new UpdateUserRequest("Updated User", null, "L001", true, UserRole.Lecturer));

        Assert.Equal(
            new[] { AuditActions.ChangeRole, AuditActions.Update }.OrderBy(action => action),
            audits.Select(audit => audit.Action).OrderBy(action => action));
        Assert.Contains(audits, audit => audit.Description == "User user: role Student -> Lecturer");
        Assert.Contains(audits, audit => audit.Description == "User user (Lecturer)");
    }

    [Fact]
    public void Users_api_and_service_do_not_expose_a_hard_delete()
    {
        Assert.DoesNotContain(typeof(UsersController).GetMethods(),
            method => method.GetCustomAttributes(typeof(HttpDeleteAttribute), true).Length > 0);
        Assert.Null(typeof(IUserService).GetMethod("DeleteAsync"));
    }

    [Fact]
    public void Users_endpoint_requires_administrator_role()
    {
        var authorize = Assert.Single(typeof(UsersController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal(Roles.Administrator, authorize.Roles);
    }

    private static Role RoleFor(UserRole role) => new() { Id = Guid.NewGuid(), Name = role.ToString() };

    private static async Task<AuditLog[]> UpdateAndGetAuditLogsAsync(
        UserRole initialRole,
        string? studentCode,
        string? lecturerCode,
        bool isActive,
        UpdateUserRequest request)
    {
        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AccessConfigDbContext(options);
        var roles = Enum.GetValues<UserRole>().Select(RoleFor).ToArray();
        var role = roles.Single(x => x.Name == initialRole.ToString());
        var user = new User
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            Username = "user",
            Email = "user@example.test",
            StudentCode = studentCode,
            LecturerCode = lecturerCode,
            FullName = "Existing User",
            IsActive = isActive
        };
        db.Roles.AddRange(roles);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new UserService(db, new TestCurrentUser(Guid.NewGuid()), TimeProvider.System,
            new StubKeycloakAdminClient(), NullLogger<UserService>.Instance);
        await service.UpdateAsync(user.Id, request, CancellationToken.None);

        return db.AuditLogs.ToArray();
    }

    private static string? UpdateConflictCode(
        User user, Guid currentUserId, UpdateUserRequest request, params User[] administrators)
        => UserService.GetUpdateConflictCode(
            user.Id,
            currentUserId,
            Enum.Parse<UserRole>(user.Role.Name),
            user.IsActive,
            request,
            administrators);

    private static User ExistingUser(UserRole role, string? studentCode, string? lecturerCode)
    {
        var roleEntity = RoleFor(role);
        return new User
        {
            Id = Guid.NewGuid(),
            RoleId = roleEntity.Id,
            Role = roleEntity,
            Username = "user",
            Email = "user@example.test",
            StudentCode = studentCode,
            LecturerCode = lecturerCode,
            FullName = "Existing User",
            IsActive = true
        };
    }

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Administrator;
        public bool IsInRole(string role) => role == Roles.Administrator;
    }

    private sealed class StubKeycloakAdminClient : IKeycloakAdminClient
    {
        public Task<Guid> CreateUserAsync(string username, string email, string firstName, string lastName,
            CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());

        public Task AssignRealmRoleAsync(Guid userId, string roleName, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> SendSetPasswordEmailAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task DeleteUserAsync(Guid userId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
