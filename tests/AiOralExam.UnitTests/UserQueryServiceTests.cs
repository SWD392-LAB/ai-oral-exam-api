using AiOralExam.Modules.AccessConfig.Application.Users;
using AiOralExam.Modules.AccessConfig.Application.Users.GetUser;
using AiOralExam.Modules.AccessConfig.Application.Users.ListUsers;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.UnitTests;

public class UserQueryServiceTests
{
    private static readonly Guid AaronId = Guid.NewGuid();
    private static readonly Guid AliceId = Guid.NewGuid();
    private static readonly Guid BobId = Guid.NewGuid();
    private static readonly Guid ZoeId = Guid.NewGuid();

    [Fact]
    public async Task Get_returns_complete_user_dto()
    {
        await using var db = CreateDatabase();
        var role = RoleFor(UserRole.Lecturer);
        var user = UserFor(
            Guid.NewGuid(), role, "Morgan Lecturer", "morgan@example.test", "morgan.l",
            true, lecturerCode: "L-200");
        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await new GetUserService(db).GetAsync(user.Id, CancellationToken.None);

        Assert.Equal(new UserDto(
            user.Id,
            "Morgan Lecturer",
            "morgan@example.test",
            "morgan.l",
            UserRole.Lecturer.ToString(),
            null,
            "L-200",
            true), result);
    }

    [Fact]
    public async Task Get_missing_user_throws_not_found_with_existing_error_code()
    {
        await using var db = CreateDatabase();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            new GetUserService(db).GetAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("user_not_found", exception.Code);
    }

    [Fact]
    public async Task List_orders_results_by_name_and_maps_all_user_fields()
    {
        await using var db = await CreateSeededDatabaseAsync();

        var result = await new ListUsersService(db).ListAsync(null, null, null, CancellationToken.None);

        Assert.Equal(
            [
                new UserDto(AaronId, "Aaron Student", "aaron@example.test", "aaron.s",
                    UserRole.Student.ToString(), "S-101", null, false),
                new UserDto(AliceId, "Alice Student", "alice@example.test", "alice.s",
                    UserRole.Student.ToString(), "S-100", null, true),
                new UserDto(BobId, "Bob Lecturer", "bob@example.test", "bob.l",
                    UserRole.Lecturer.ToString(), null, "L-200", false),
                new UserDto(ZoeId, "Zoe Admin", "zoe@example.test", "zoe.a",
                    UserRole.Administrator.ToString(), null, null, true)
            ],
            result);
    }

    [Fact]
    public async Task List_filters_by_role()
    {
        await using var db = await CreateSeededDatabaseAsync();

        var result = await new ListUsersService(db).ListAsync(
            UserRole.Student, null, null, CancellationToken.None);

        Assert.Equal([AaronId, AliceId], result.Select(user => user.Id));
    }

    [Fact]
    public async Task List_filters_by_active_status()
    {
        await using var db = await CreateSeededDatabaseAsync();

        var result = await new ListUsersService(db).ListAsync(
            null, false, null, CancellationToken.None);

        Assert.Equal([AaronId, BobId], result.Select(user => user.Id));
    }

    [Fact]
    public async Task List_applies_role_and_active_filters_together()
    {
        await using var db = await CreateSeededDatabaseAsync();

        var result = await new ListUsersService(db).ListAsync(
            UserRole.Student, true, null, CancellationToken.None);

        Assert.Equal([AliceId], result.Select(user => user.Id));
    }

    private static AccessConfigDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AccessConfigDbContext(options);
    }

    private static async Task<AccessConfigDbContext> CreateSeededDatabaseAsync()
    {
        var db = CreateDatabase();
        var student = RoleFor(UserRole.Student);
        var lecturer = RoleFor(UserRole.Lecturer);
        var administrator = RoleFor(UserRole.Administrator);

        db.Roles.AddRange(student, lecturer, administrator);
        db.Users.AddRange(
            UserFor(AaronId, student, "Aaron Student", "aaron@example.test", "aaron.s",
                false, studentCode: "S-101"),
            UserFor(AliceId, student, "Alice Student", "alice@example.test", "alice.s",
                true, studentCode: "S-100"),
            UserFor(BobId, lecturer, "Bob Lecturer", "bob@example.test", "bob.l",
                false, lecturerCode: "L-200"),
            UserFor(ZoeId, administrator, "Zoe Admin", "zoe@example.test", "zoe.a", true));
        await db.SaveChangesAsync();
        return db;
    }

    private static Role RoleFor(UserRole role)
        => new() { Id = Guid.NewGuid(), Name = role.ToString() };

    private static User UserFor(
        Guid id,
        Role role,
        string fullName,
        string email,
        string username,
        bool isActive,
        string? studentCode = null,
        string? lecturerCode = null)
        => new()
        {
            Id = id,
            RoleId = role.Id,
            Role = role,
            FullName = fullName,
            Email = email,
            Username = username,
            StudentCode = studentCode,
            LecturerCode = lecturerCode,
            IsActive = isActive
        };
}
