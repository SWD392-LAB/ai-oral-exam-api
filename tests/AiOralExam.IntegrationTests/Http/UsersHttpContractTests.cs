using System.Net;
using System.Net.Http.Json;
using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.Users;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AiOralExam.IntegrationTests.Http;

public sealed class UsersHttpContractTests
{
    private static readonly Guid AdministratorId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222221");
    private static readonly Guid TargetStudentId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222222");
    private static readonly Guid StudentRoleId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222223");
    private static readonly Guid LecturerRoleId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222224");
    private static readonly Guid AdministratorRoleId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222225");

    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/admin/users")]
    public async Task Post_user_aliases_create_user_and_return_item_location(string route)
    {
        using var factory = NewFactory(Roles.Administrator);
        await SeedUsersAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(route, new
        {
            fullName = "  New Student  ",
            email = "New.Student@example.test",
            role = "Student",
            studentCode = "S-200",
            lecturerCode = (string?)null,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedUserDto>();
        Assert.NotNull(created);
        Assert.Equal(Guid.Parse("7b4b6116-0d14-4be7-8e15-76f0886de301"), created.Id);
        Assert.Equal("New Student", created.FullName);
        Assert.Equal("new.student@example.test", created.Email);
        Assert.Equal("new.student", created.Username);
        Assert.Equal(Roles.Student, created.Role);
        Assert.True(created.IsActive);
        Assert.False(created.SetupEmailSent);
        Assert.EndsWith($"/api/users/{created.Id}", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/admin/users")]
    public async Task Get_user_list_aliases_return_user_dtos(string route)
    {
        using var factory = NewFactory(Roles.Administrator);
        await SeedUsersAsync(factory, StudentSeed());
        using var client = factory.CreateClient();

        var users = await client.GetFromJsonAsync<List<UserDto>>(route);

        Assert.NotNull(users);
        var student = Assert.Single(users, user => user.Id == TargetStudentId);
        Assert.Equal("Test Student", student.FullName);
        Assert.Equal("student@example.test", student.Email);
        Assert.Equal(Roles.Student, student.Role);
        Assert.Equal("S-100", student.StudentCode);
        Assert.True(student.IsActive);
    }

    [Fact]
    public async Task Get_user_by_id_returns_user_dto()
    {
        using var factory = NewFactory(Roles.Administrator);
        await SeedUsersAsync(factory, StudentSeed());
        using var client = factory.CreateClient();

        var user = await client.GetFromJsonAsync<UserDto>($"/api/users/{TargetStudentId}");

        Assert.NotNull(user);
        Assert.Equal(TargetStudentId, user.Id);
        Assert.Equal("Test Student", user.FullName);
        Assert.Equal("student@example.test", user.Email);
        Assert.Equal(Roles.Student, user.Role);
        Assert.Equal("S-100", user.StudentCode);
    }

    [Fact]
    public async Task Put_user_updates_and_returns_user_dto()
    {
        using var factory = NewFactory(Roles.Administrator);
        await SeedUsersAsync(factory, StudentSeed());
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"/api/users/{TargetStudentId}", new
        {
            fullName = "Updated Student",
            studentCode = "S-100",
            lecturerCode = (string?)null,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserDto>();
        Assert.NotNull(user);
        Assert.Equal(TargetStudentId, user.Id);
        Assert.Equal("Updated Student", user.FullName);
        Assert.Equal("student@example.test", user.Email);
        Assert.Equal(Roles.Student, user.Role);
        Assert.Equal("S-100", user.StudentCode);
        Assert.True(user.IsActive);
    }

    [Fact]
    public async Task Get_missing_user_returns_shared_not_found_error_envelope()
    {
        using var factory = NewFactory(Roles.Administrator);
        await SeedUsersAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/users/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("user_not_found", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
    }

    [Fact]
    public async Task Lecturer_cannot_access_administrator_user_list()
    {
        using var factory = NewFactory(Roles.Lecturer, Guid.Parse("d115d5a8-63f7-49f3-9492-222222222226"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static ApiTestFactory NewFactory(string role, Guid? userId = null) =>
        new ApiTestFactory().AuthenticateAs(userId ?? AdministratorId, role);

    private static UserSeed StudentSeed() => new(
        TargetStudentId, Roles.Student, "Test Student", "student@example.test", "test.student", "S-100", null);

    private static async Task SeedUsersAsync(ApiTestFactory factory, params UserSeed[] additionalUsers)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var roles = new Dictionary<string, Role>
        {
            [Roles.Student] = new() { Id = StudentRoleId, Name = Roles.Student },
            [Roles.Lecturer] = new() { Id = LecturerRoleId, Name = Roles.Lecturer },
            [Roles.Administrator] = new() { Id = AdministratorRoleId, Name = Roles.Administrator },
        };
        db.Roles.AddRange(roles.Values);
        db.Users.Add(CreateUser(
            factory.AuthenticatedUserId,
            roles[factory.AuthenticatedRole],
            "Test Administrator",
            "administrator@example.test",
            "test.administrator"));
        db.Users.AddRange(additionalUsers.Select(seed => CreateUser(
            seed.Id, roles[seed.Role], seed.FullName, seed.Email, seed.Username, seed.StudentCode, seed.LecturerCode)));
        await db.SaveChangesAsync();
    }

    private static User CreateUser(
        Guid id, Role role, string fullName, string email, string username,
        string? studentCode = null, string? lecturerCode = null) => new()
    {
        Id = id,
        RoleId = role.Id,
        Role = role,
        FullName = fullName,
        Email = email,
        Username = username,
        StudentCode = studentCode,
        LecturerCode = lecturerCode,
        IsActive = true,
        CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private sealed record UserSeed(
        Guid Id, string Role, string FullName, string Email, string Username,
        string? StudentCode, string? LecturerCode);
}
