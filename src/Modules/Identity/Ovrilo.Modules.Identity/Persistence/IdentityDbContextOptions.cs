using Microsoft.EntityFrameworkCore;

namespace Ovrilo.Modules.Identity.Persistence;

internal static class IdentityDbContextOptions
{
    public const string ConnectionStringName = "Ovrilo";

    public static void Configure(DbContextOptionsBuilder builder, string? connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never connects at design time.</summary>
internal sealed class DesignTimeIdentityDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityDbContextOptions.Configure(builder, "Host=localhost;Database=ovrilo_design_time");
        return new IdentityDbContext(builder.Options);
    }
}
