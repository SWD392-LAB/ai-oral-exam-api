using AiOralExam.Modules.AccessConfig.Application.AIServices;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.UnitTests;

public class AiConfigServiceTests
{
    [Fact]
    public async Task Saving_active_configuration_with_blank_key_keeps_existing_ciphertext()
    {
        await using var db = CreateDbContext();
        var configuration = AddConfiguration(db, "stored-ciphertext");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.SaveActiveAsync(CreateRequest(" \t"), CancellationToken.None);
        var listed = Assert.Single(await service.ListAsync(CancellationToken.None));

        Assert.Equal("stored-ciphertext", configuration.ApiKeyEncrypted);
        Assert.True(result.HasApiKey);
        Assert.True(listed.HasApiKey);
    }

    [Fact]
    public async Task Updating_configuration_with_new_key_replaces_ciphertext()
    {
        await using var db = CreateDbContext();
        var configuration = AddConfiguration(db, "stored-ciphertext");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.SaveAsync(configuration.Id, CreateRequest("new-api-key"), CancellationToken.None);

        Assert.NotEqual("stored-ciphertext", configuration.ApiKeyEncrypted);
        Assert.True(result.HasApiKey);
    }

    [Fact]
    public async Task Updating_provider_without_new_key_is_rejected()
    {
        await using var db = CreateDbContext();
        var configuration = AddConfiguration(db, "stored-ciphertext");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SaveAsync(configuration.Id, CreateRequest(" ", provider: "Other Provider"), CancellationToken.None));

        Assert.Equal("A new API key is required when the provider or endpoint changes.",
            Assert.Single(exception.Details!["ApiKey"]));
    }

    [Fact]
    public async Task Updating_endpoint_without_new_key_is_rejected()
    {
        await using var db = CreateDbContext();
        var configuration = AddConfiguration(db, "stored-ciphertext");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SaveAsync(configuration.Id, CreateRequest(" ", endpoint: "https://other.test"), CancellationToken.None));

        Assert.Equal("A new API key is required when the provider or endpoint changes.",
            Assert.Single(exception.Details!["ApiKey"]));
    }

    [Fact]
    public async Task Updating_provider_with_new_key_is_accepted_and_replaces_key()
    {
        await using var db = CreateDbContext();
        var configuration = AddConfiguration(db, "stored-ciphertext");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.SaveAsync(
            configuration.Id, CreateRequest("new-api-key", provider: "Other Provider"), CancellationToken.None);

        Assert.NotEqual("stored-ciphertext", configuration.ApiKeyEncrypted);
        Assert.True(result.HasApiKey);
    }

    [Fact]
    public async Task Updating_same_provider_and_endpoint_with_blank_key_keeps_key()
    {
        await using var db = CreateDbContext();
        var configuration = AddConfiguration(db, "stored-ciphertext");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.SaveAsync(configuration.Id, CreateRequest(" "), CancellationToken.None);

        Assert.Equal("stored-ciphertext", configuration.ApiKeyEncrypted);
    }

    [Fact]
    public async Task Updating_case_and_trailing_slash_only_differences_keeps_key()
    {
        await using var db = CreateDbContext();
        var configuration = AddConfiguration(db, "stored-ciphertext");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.SaveAsync(configuration.Id,
            CreateRequest(" ", provider: "pROVIDER", endpoint: "HTTPS://EXAMPLE.TEST/"), CancellationToken.None);

        Assert.Equal("stored-ciphertext", configuration.ApiKeyEncrypted);
    }

    [Fact]
    public async Task Creating_configuration_with_blank_key_is_rejected()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SaveAsync(null, CreateRequest(" \t"), CancellationToken.None));

        Assert.Equal("API key is required.", Assert.Single(exception.Details!["ApiKey"]));
        Assert.Empty(db.AiServiceConfigs);
    }

    [Fact]
    public async Task Saving_active_configuration_without_existing_row_requires_key()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SaveActiveAsync(CreateRequest(null), CancellationToken.None));

        Assert.Equal("API key is required.", Assert.Single(exception.Details!["ApiKey"]));
        Assert.Empty(db.AiServiceConfigs);
    }

    private static AiServiceConfig AddConfiguration(AccessConfigDbContext db, string encryptedKey)
    {
        var configuration = new AiServiceConfig
        {
            Id = Guid.NewGuid(),
            ConfiguredBy = Guid.NewGuid(),
            ServiceType = AiServiceType.STT,
            ProviderName = "Provider",
            EndpointUrl = "https://example.test",
            ApiKeyEncrypted = encryptedKey,
            Language = SpeechLanguage.English,
            IsActive = true
        };
        db.AiServiceConfigs.Add(configuration);
        return configuration;
    }

    private static SaveAiServiceConfigRequest CreateRequest(
        string? apiKey, string provider = "Provider", string endpoint = "https://example.test")
        => new(AiServiceType.STT, provider, endpoint, apiKey, SpeechLanguage.English);

    private static AiConfigService CreateService(AccessConfigDbContext db)
        => new(db, new TestCurrentUser(Guid.NewGuid()), new EphemeralDataProtectionProvider());

    private static AccessConfigDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Administrator;
        public bool IsInRole(string role) => role == Roles.Administrator;
    }
}
