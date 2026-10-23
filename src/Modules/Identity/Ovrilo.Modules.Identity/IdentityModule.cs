using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Identity.Domain;
using Ovrilo.Modules.Identity.Features;
using Ovrilo.Modules.Identity.Persistence;
using Ovrilo.Modules.Identity.Tokens;

namespace Ovrilo.Modules.Identity;

/// <summary>Composition entry points for the Identity module. The host calls these; other modules use <see cref="IIdentityModule"/>.</summary>
public static class IdentityModule
{
    /// <summary>Rate limiter policy applied to anonymous credential endpoints.</summary>
    public const string AuthRateLimitPolicy = "identity-auth";

    private const string RateLimitSection = "Identity:RateLimit";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.Schema);

        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<DummyPasswordHash>();
        services.AddSingleton<AccessTokenIssuer>();
        services.AddScoped<SessionIssuer>();
        services.AddScoped<IIdentityModule, IdentityModuleApi>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = AccessTokenIssuer.CreateSigningKey(jwt),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = IdentityClaims.UserId,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });
        services.AddAuthorization();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthRateLimitPolicy, context =>
            {
                var section = context.RequestServices.GetRequiredService<IConfiguration>().GetSection(RateLimitSection);
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = section.GetValue("PermitLimit", 10),
                        Window = TimeSpan.FromSeconds(section.GetValue("WindowSeconds", 60)),
                        QueueLimit = 0,
                    });
            });
        });

        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/register", Register.HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithName("Register")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Creates an account and signs it in on this device.")
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/login", Login.HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithName("Login")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("Signs in with a username or email and password.")
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/refresh", Refresh.HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithName("RefreshSession")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("Rotates the refresh token and issues a new access token.")
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/logout", SignOut.CurrentAsync)
            .RequireAuthorization()
            .WithName("Logout")
            .WithSummary("Revokes the current session.");

        auth.MapPost("/logout-all", SignOut.EverywhereAsync)
            .RequireAuthorization()
            .WithName("LogoutAll")
            .WithSummary("Revokes every session of the current account.");

        endpoints.MapGet("/users/me", GetCurrentUser.HandleAsync)
            .RequireAuthorization()
            .WithTags("Users")
            .WithName("GetCurrentUser")
            .WithSummary("Returns the authenticated account.");

        endpoints.MapPut("/users/me/avatar", Avatar.SetAsync)
            .RequireAuthorization()
            .WithTags("Users")
            .WithName("SetAvatar")
            .WithSummary("Sets the profile picture to a completed avatar upload. The previous picture is deleted.");

        endpoints.MapDelete("/users/me/avatar", Avatar.RemoveAsync)
            .RequireAuthorization()
            .WithTags("Users")
            .WithName("RemoveAvatar")
            .WithSummary("Removes the profile picture; initials are shown instead.");

        return endpoints;
    }

    /// <summary>Applies pending Identity schema migrations. Development/test only; production migrations are explicit operations.</summary>
    public static Task MigrateIdentityDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        services.MigrateModuleAsync<IdentityDbContext>(cancellationToken);
}

internal sealed class IdentityModuleApi(IdentityDbContext db) : IIdentityModule
{
    public Task<PublicUser?> GetPublicUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.Status == UserStatus.Active)
            .Select(u => new PublicUser(u.Id, u.Username, u.DisplayName, u.AvatarAssetId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, PublicUser>> GetPublicUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        return await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.Status == UserStatus.Active)
            .Select(u => new PublicUser(u.Id, u.Username, u.DisplayName, u.AvatarAssetId))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
    }

    public Task<PublicUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(username);
        // Usernames are stored normalized (lowercase).
        var normalized = username.Trim().ToLowerInvariant();
        return db.Users.AsNoTracking()
            .Where(u => u.Username == normalized && u.Status == UserStatus.Active)
            .Select(u => new PublicUser(u.Id, u.Username, u.DisplayName, u.AvatarAssetId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<bool> IsAccountActiveAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.Users.AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken);

    public async Task<Availability> GetAvailabilityAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.Status == UserStatus.Active)
            .Select(u => (Availability?)u.Availability)
            .SingleOrDefaultAsync(cancellationToken) ?? Availability.Online;

    public async Task<bool> SetAvailabilityAsync(Guid userId, Availability availability, CancellationToken cancellationToken = default) =>
        await db.Users
            .Where(u => u.Id == userId && u.Status == UserStatus.Active)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.Availability, availability), cancellationToken) == 1;
}
