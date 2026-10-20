using System.Text.Json;
using System.Text.Json;
using AiOralExam.Modules.AccessConfig.Application.Auth;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.UnitTests;

public class AuthServiceTests
{
    [Fact]
    public async Task Get_profile_returns_persisted_fields_and_expected_json_properties()
    {
        var profile = await GetProfileAsync(isActive: true);

        Assert.Equal("student.a1", profile.Username);
        Assert.NotEqual(profile.Email, profile.Username);
        Assert.True(profile.IsActive);
        Assert.Equal("Nguyen Van An", profile.FullName);
        Assert.Equal("an@example.test", profile.Email);
        Assert.Equal("Student", profile.Role);
        Assert.Equal("S001", profile.StudentCode);
        Assert.Null(profile.LecturerCode);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            profile, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var response = json.RootElement;
        Assert.Equal(profile.Id, response.GetProperty("id").GetGuid());
        Assert.Equal("Nguyen Van An", response.GetProperty("fullName").GetString());
        Assert.Equal("an@example.test", response.GetProperty("email").GetString());
        Assert.Equal("Student", response.GetProperty("role").GetString());
        Assert.Equal("S001", response.GetProperty("studentCode").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("lecturerCode").ValueKind);
        Assert.Equal("student.a1", response.GetProperty("username").GetString());
        Assert.True(response.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Get_profile_returns_persisted_inactive_state()
    {
        var profile = await GetProfileAsync(isActive: false);

        Assert.False(profile.IsActive);
    }

    private static async Task<UserProfileDto> GetProfileAsync(bool isActive)
    {
        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AccessConfigDbContext(options);
        var role = new Role { Id = Guid.NewGuid(), Name = "Student" };
        var user = new User
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            FullName = "Nguyen Van An",
            Email = "an@example.test",
            Username = "student.a1",
            StudentCode = "S001",
            IsActive = isActive
        };
        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return await new AuthService(db).GetProfileAsync(user.Id);
    }
}
