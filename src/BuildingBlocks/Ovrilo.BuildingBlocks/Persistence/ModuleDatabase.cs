using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ovrilo.BuildingBlocks.Persistence;

/// <summary>Shared PostgreSQL conventions for module-owned DbContexts (ADR-0004, ADR-0005).</summary>
public static class ModuleDatabase
{
    public const string ConnectionStringName = "Ovrilo";

    /// <summary>Npgsql with snake_case names and a migrations history table inside the module's own schema.</summary>
    public static DbContextOptionsBuilder UseModulePostgres(this DbContextOptionsBuilder builder, string? connectionString, string schema) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", schema))
            .UseSnakeCaseNamingConvention();

    /// <summary>Registers a module DbContext against the shared <c>ConnectionStrings:Ovrilo</c> database.</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : DbContext =>
        services.AddDbContext<TContext>((provider, options) =>
            options.UseModulePostgres(
                provider.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName),
                schema));

    /// <summary>Applies pending migrations for one module. Development/test only; production migrations are explicit operations.</summary>
    public static async Task MigrateModuleAsync<TContext>(this IServiceProvider services, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Options for <c>dotnet ef</c> design-time factories; never connects.</summary>
    public static DbContextOptions<TContext> DesignTimeOptions<TContext>(string schema)
        where TContext : DbContext =>
        (DbContextOptions<TContext>)new DbContextOptionsBuilder<TContext>()
            .UseModulePostgres("Host=localhost;Database=ovrilo_design_time", schema)
            .Options;

    /// <summary>
    /// Current UTC time truncated to PostgreSQL <c>timestamptz</c> precision (1 µs), so values returned
    /// before and after a database round trip are identical.
    /// </summary>
    public static DateTimeOffset GetDatabaseUtcNow(this TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var now = timeProvider.GetUtcNow();
        return now.AddTicks(-(now.Ticks % (TimeSpan.TicksPerMillisecond / 1000)));
    }

    /// <summary>True when the exception is a PostgreSQL unique violation, optionally on a specific constraint/index.</summary>
    public static bool IsUniqueViolation(this DbUpdateException exception, string? constraintName = null) =>
        exception?.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } pg
        && (constraintName is null || string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal));
}
