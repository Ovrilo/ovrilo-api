using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Ovrilo.Api.Tests.Media;

using Testcontainers.PostgreSql;

namespace Ovrilo.Api.Tests;

/// <summary>
/// Runs the API against disposable PostgreSQL and RustFS containers with test-only secrets.
/// Shared by all tests in <see cref="SharedApiFixture"/>; tests must use unique data.
/// </summary>
public sealed class OvriloApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestSigningKey = "integration-test-signing-key-not-a-secret-0123456789";
    public const string TestLiveKitUrl = "ws://livekit.test:7880";
    public const string TestLiveKitKey = "test-key";
    public const string TestLiveKitSecret = "integration-test-livekit-secret-not-a-secret-0123";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.11-alpine").Build();

    public StorageContainer Storage { get; } = StorageContainer.RustFs();

    public Task InitializeAsync() => Task.WhenAll(_postgres.StartAsync(), Storage.StartAsync());

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await Storage.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Ovrilo", _postgres.GetConnectionString());
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
        builder.UseSetting("Identity:Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("Identity:RateLimit:PermitLimit", "10000");
        builder.UseSetting("Messaging:RateLimit:PermitLimit", "10000");
        builder.UseSetting("Realtime:PresenceGraceSeconds", "0");
        builder.UseSetting("VoiceSessions:LiveKit:ServerUrl", TestLiveKitUrl);
        builder.UseSetting("VoiceSessions:LiveKit:ApiKey", TestLiveKitKey);
        builder.UseSetting("VoiceSessions:LiveKit:ApiSecret", TestLiveKitSecret);
        builder.UseSetting("Media:Storage:ServiceUrl", Storage.ServiceUrl.ToString());
        builder.UseSetting("Media:Storage:Bucket", StorageContainer.Bucket);
        builder.UseSetting("Media:Storage:AccessKey", StorageContainer.AccessKey);
        builder.UseSetting("Media:Storage:SecretKey", StorageContainer.SecretKey);
        builder.UseSetting("Media:RateLimit:PermitLimit", "10000");
        // Tests run cleanup passes explicitly.
        builder.UseSetting("Media:Janitor:Enabled", "false");
    }
}

[CollectionDefinition(Name)]
public sealed class SharedApiFixture : ICollectionFixture<OvriloApiFactory>
{
    public const string Name = "api";
}
