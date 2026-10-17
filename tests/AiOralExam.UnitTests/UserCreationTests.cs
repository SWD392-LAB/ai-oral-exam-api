using AiOralExam.Modules.AccessConfig.Application.Keycloak;
using AiOralExam.Modules.AccessConfig.Application.Users;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiOralExam.UnitTests;

public class UserCreationTests
{
    private static readonly Guid CurrentUserId = Guid.NewGuid();
    private static readonly Guid KeycloakUserId = Guid.NewGuid();

    [Fact]
    public async Task Create_saves_keycloak_identity_and_audit_then_sends_setup_email()
    {
        await using var db = await CreateDatabaseAsync();
        var keycloak = new FakeKeycloakAdminClient();
        var service = CreateService(db, keycloak);

        var result = await service.CreateAsync(Request(), CancellationToken.None);

        var savedUser = await db.Users.SingleAsync();
        Assert.Equal(KeycloakUserId, result.Id);
        Assert.Equal("han.hg", result.Username);
        Assert.True(result.SetupEmailSent);
        Assert.Equal(KeycloakUserId, savedUser.Id);
        Assert.Equal("han.hg", savedUser.Username);
        Assert.Equal("han.hg@aives.edu.vn", savedUser.Email);
        Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal(
            ["create:han.hg", "assign:Administrator", "email"],
            keycloak.Calls);
    }

    [Fact]
    public async Task Create_reports_failed_setup_email_without_rolling_back_user()
    {
        await using var db = await CreateDatabaseAsync();
        var keycloak = new FakeKeycloakAdminClient { EmailSent = false };
        var service = CreateService(db, keycloak);

        var result = await service.CreateAsync(Request(), CancellationToken.None);

        Assert.False(result.SetupEmailSent);
        Assert.Single(await db.Users.ToListAsync());
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Get_user_returns_username_from_database()
    {
        await using var db = await CreateDatabaseAsync();
        var role = await db.Roles.SingleAsync();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            RoleId = role.Id,
            Role = role,
            FullName = "Nguyen Van An",
            Email = "nguyen.van.an@example.test",
            Username = "an.nv",
            IsActive = true
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeKeycloakAdminClient());

        var result = await service.GetAsync(userId, CancellationToken.None);

        Assert.Equal("an.nv", result.Username);
    }

    [Fact]
    public async Task Update_own_profile_rejects_empty_full_name()
    {
        await using var db = await CreateDatabaseAsync();
        await AddUserAsync(db, CurrentUserId);
        var service = CreateService(db, new FakeKeycloakAdminClient());

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateOwnProfileAsync(CurrentUserId, new UpdateOwnProfileRequest("  "), CancellationToken.None));

        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Resend_setup_email_rejects_inactive_user_without_keycloak_call_or_audit()
    {
        await using var db = await CreateDatabaseAsync();
        await AddUserAsync(db);
        var user = await db.Users.SingleAsync();
        user.IsActive = false;
        await db.SaveChangesAsync();
        var keycloak = new FakeKeycloakAdminClient();
        var service = CreateService(db, keycloak);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.ResendSetupEmailAsync(KeycloakUserId, CurrentUserId, CancellationToken.None));

        Assert.Equal("user_inactive", exception.Code);
        Assert.Empty(keycloak.Calls);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Update_own_profile_changes_only_full_name_and_writes_audit()
    {
        await using var db = await CreateDatabaseAsync();
        await AddUserAsync(db, CurrentUserId);
        var service = CreateService(db, new FakeKeycloakAdminClient());
        var user = await db.Users.SingleAsync();
        var email = user.Email;
        var username = user.Username;
        var roleId = user.RoleId;
        var isActive = user.IsActive;
        var studentCode = user.StudentCode;
        var lecturerCode = user.LecturerCode;

        await service.UpdateOwnProfileAsync(
            CurrentUserId, new UpdateOwnProfileRequest("  Updated Name  "), CancellationToken.None);

        Assert.Equal("Updated Name", user.FullName);
        Assert.Equal(email, user.Email);
        Assert.Equal(username, user.Username);
        Assert.Equal(roleId, user.RoleId);
        Assert.Equal(isActive, user.IsActive);
        Assert.Equal(studentCode, user.StudentCode);
        Assert.Equal(lecturerCode, user.LecturerCode);
        var audit = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal(AuditActions.UpdateOwnProfile, audit.Action);
        Assert.Equal(AuditEntities.User, audit.TargetEntity);
        Assert.Equal(CurrentUserId, audit.TargetId);
    }

    [Fact]
    public async Task Resend_setup_email_succeeds_and_writes_one_audit_row()
    {
        await using var db = await CreateDatabaseAsync();
        await AddUserAsync(db);
        var keycloak = new FakeKeycloakAdminClient();
        var service = CreateService(db, keycloak);

        await service.ResendSetupEmailAsync(KeycloakUserId, CurrentUserId, CancellationToken.None);

        var audit = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal(AuditActions.ResendSetupEmail, audit.Action);
        Assert.Equal(AuditEntities.User, audit.TargetEntity);
        Assert.Equal(KeycloakUserId, audit.TargetId);
        Assert.Equal(["email"], keycloak.Calls);
    }

    [Fact]
    public async Task Resend_setup_email_failure_returns_setup_email_failed_without_audit()
    {
        await using var db = await CreateDatabaseAsync();
        await AddUserAsync(db);
        var keycloak = new FakeKeycloakAdminClient { EmailSent = false };
        var service = CreateService(db, keycloak);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            service.ResendSetupEmailAsync(KeycloakUserId, CurrentUserId, CancellationToken.None));

        Assert.Equal(502, exception.StatusCode);
        Assert.Equal("setup_email_failed", exception.Code);
        Assert.Empty(await db.AuditLogs.ToListAsync());
        Assert.Equal(["email"], keycloak.Calls);
    }

    [Fact]
    public async Task Resend_setup_email_returns_not_found_for_unknown_user()
    {
        await using var db = await CreateDatabaseAsync();
        var keycloak = new FakeKeycloakAdminClient();
        var service = CreateService(db, keycloak);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.ResendSetupEmailAsync(KeycloakUserId, CurrentUserId, CancellationToken.None));

        Assert.Equal("user_not_found", exception.Code);
        Assert.Empty(keycloak.Calls);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Database_failure_deletes_keycloak_user_and_rethrows_original_exception()
    {
        var failure = new DbUpdateException("database unavailable");
        var interceptor = new ThrowOnSaveInterceptor(failure);
        await using var db = await CreateDatabaseAsync(interceptor);
        var keycloak = new FakeKeycloakAdminClient();
        var service = CreateService(db, keycloak);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.CreateAsync(Request(), CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Contains("delete", keycloak.Calls);
        Assert.DoesNotContain("email", keycloak.Calls);
    }

    [Fact]
    public async Task Role_assignment_failure_deletes_keycloak_user_without_saving_user_row()
    {
        await using var db = await CreateDatabaseAsync();
        var keycloak = new FakeKeycloakAdminClient
        {
            AssignException = new InvalidOperationException("role assignment failed")
        };
        var service = CreateService(db, keycloak);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(Request(), CancellationToken.None));

        Assert.Contains("delete", keycloak.Calls);
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
        Assert.DoesNotContain("email", keycloak.Calls);
    }

    [Fact]
    public async Task Duplicate_email_is_rejected_before_calling_keycloak()
    {
        await using var db = await CreateDatabaseAsync();
        db.Users.Add(ExistingUser("han.hg@aives.edu.vn", "existing"));
        await db.SaveChangesAsync();
        var keycloak = new FakeKeycloakAdminClient();
        var service = CreateService(db, keycloak);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync(Request(), CancellationToken.None));

        Assert.Equal("email_exists", exception.Code);
        Assert.Empty(keycloak.Calls);
    }

    [Fact]
    public async Task Duplicate_derived_username_is_rejected_before_calling_keycloak()
    {
        await using var db = await CreateDatabaseAsync();
        db.Users.Add(ExistingUser("other@aives.edu.vn", "han.hg"));
        await db.SaveChangesAsync();
        var keycloak = new FakeKeycloakAdminClient();
        var service = CreateService(db, keycloak);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync(Request(), CancellationToken.None));

        Assert.Equal("username_exists", exception.Code);
        Assert.Empty(keycloak.Calls);
    }

    [Fact]
    public async Task Keycloak_failure_does_not_save_user_or_audit()
    {
        await using var db = await CreateDatabaseAsync();
        var keycloak = new FakeKeycloakAdminClient
        {
            CreateException = new AppException(502, "keycloak_unavailable", "Keycloak is unavailable.")
        };
        var service = CreateService(db, keycloak);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            service.CreateAsync(Request(), CancellationToken.None));

        Assert.Equal("keycloak_unavailable", exception.Code);
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    private static CreateUserRequest Request()
        => new("Han Hoang Giang", "HAN.HG@aives.edu.vn", UserRole.Administrator, null, null);

    private static async Task<AccessConfigDbContext> CreateDatabaseAsync(
        SaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor is null ? [] : [interceptor])
            .Options;
        var db = new AccessConfigDbContext(options);
        db.Roles.Add(new Role { Id = Guid.NewGuid(), Name = UserRole.Administrator.ToString() });
        await db.SaveChangesAsync();
        if (interceptor is ThrowOnSaveInterceptor throwOnSave)
            throwOnSave.Enabled = true;
        return db;
    }

    private static UserService CreateService(AccessConfigDbContext db, FakeKeycloakAdminClient keycloak)
        => new(db, new CurrentUser(), TimeProvider.System, keycloak, NullLogger<UserService>.Instance);

    private static async Task AddUserAsync(AccessConfigDbContext db, Guid? userId = null)
    {
        var role = await db.Roles.SingleAsync();
        db.Users.Add(new User
        {
            Id = userId ?? KeycloakUserId,
            RoleId = role.Id,
            Role = role,
            FullName = "Han Hoang Giang",
            Email = "han.hg@aives.edu.vn",
            Username = "han.hg",
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    private static User ExistingUser(string email, string username)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = UserRole.Administrator.ToString() };
        return new User
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            FullName = "Existing User",
            Email = email,
            Username = username,
            IsActive = true
        };
    }

    private sealed class FakeKeycloakAdminClient : IKeycloakAdminClient
    {
        public List<string> Calls { get; } = [];
        public Exception? CreateException { get; init; }
        public Exception? AssignException { get; init; }
        public bool EmailSent { get; init; } = true;

        public Task<Guid> CreateUserAsync(
            string username, string email, string firstName, string lastName, CancellationToken ct = default)
        {
            Calls.Add($"create:{username}");
            if (CreateException is not null)
                throw CreateException;
            return Task.FromResult(KeycloakUserId);
        }

        public Task AssignRealmRoleAsync(Guid userId, string roleName, CancellationToken ct = default)
        {
            Calls.Add($"assign:{roleName}");
            if (AssignException is not null)
                throw AssignException;
            return Task.CompletedTask;
        }

        public Task<bool> SendSetPasswordEmailAsync(Guid userId, CancellationToken ct = default)
        {
            Calls.Add("email");
            return Task.FromResult(EmailSent);
        }

        public Task DeleteUserAsync(Guid userId, CancellationToken ct = default)
        {
            Calls.Add("delete");
            return Task.CompletedTask;
        }
    }

    private sealed class CurrentUser : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => CurrentUserId;
        public string? Role => UserRole.Administrator.ToString();
        public bool IsInRole(string role) => role == Role;
    }

    private sealed class ThrowOnSaveInterceptor(DbUpdateException exception) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled)
                throw exception;
            return ValueTask.FromResult(result);
        }
    }
}
