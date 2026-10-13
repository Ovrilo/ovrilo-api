using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Testcontainers.PostgreSql;

namespace Ovrilo.Api.Tests;

/// <summary>
/// Runs the API against a disposable PostgreSQL container with test-only secrets.
/// Shared by all tests in <see cref="SharedApiFixture"/>; tests must use unique data.
/// </summary>
public sealed class OvriloApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestSigningKey = "integration-test-signing-key-not-a-secret-0123456789";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.11-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    Task IAsyncLifetime.DisposeAsync() => _postgres.DisposeAsync().AsTask();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Ovrilo", _postgres.GetConnectionString());
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
        builder.UseSetting("Identity:Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("Identity:RateLimit:PermitLimit", "10000");
    }
}

[CollectionDefinition(Name)]
public sealed class SharedApiFixture : ICollectionFixture<OvriloApiFactory>
{
    public const string Name = "api";
}
