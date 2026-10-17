using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Social.Contracts;
using Ovrilo.Modules.Social.Domain;
using Ovrilo.Modules.Social.Features;
using Ovrilo.Modules.Social.Persistence;

namespace Ovrilo.Modules.Social;

/// <summary>Composition entry points for the Social module: friendships, requests and blocks (docs/domain/social.md).</summary>
public static class SocialModule
{
    /// <summary>Per-user limit on sending friend requests (spam).</summary>
    public const string RequestRateLimitPolicy = "social-requests";

    private const string RateLimitSection = "Social:RateLimit";

    public static IServiceCollection AddSocialModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<SocialDbContext>(SocialDbContext.Schema);
        services.AddScoped<ISocialModule, SocialModuleApi>();
        services.AddRateLimiter(options => options.AddPolicy(RequestRateLimitPolicy, context =>
        {
            var section = context.RequestServices.GetRequiredService<IConfiguration>().GetSection(RateLimitSection);
            var user = context.User.FindFirst(IdentityClaims.UserId)?.Value ?? "anonymous";
            return RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = section.GetValue("PermitLimit", 10),
                Window = TimeSpan.FromSeconds(section.GetValue("WindowSeconds", 600)),
                QueueLimit = 0,
            });
        }));
        return services;
    }

    public static IEndpointRouteBuilder MapSocialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var friends = endpoints.MapGroup("/friends").WithTags("Friends").RequireAuthorization();

        friends.MapGet("/", FriendEndpoints.ListFriendsAsync)
            .WithName("ListFriends")
            .WithSummary("Your friends, sorted by display name.");

        friends.MapDelete("/{userId:guid}", FriendEndpoints.RemoveFriendAsync)
            .WithName("RemoveFriend")
            .WithSummary("Ends a friendship. Idempotent.");

        friends.MapGet("/requests", FriendEndpoints.ListRequestsAsync)
            .WithName("ListFriendRequests")
            .WithSummary("Pending friend requests you received and sent, newest first.");

        friends.MapPost("/requests", FriendEndpoints.SendRequestAsync)
            .RequireRateLimiting(RequestRateLimitPolicy)
            .WithName("SendFriendRequest")
            .WithSummary("Sends a friend request by username. If they already asked you, you become friends (200, outcome friends).")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        friends.MapPost("/requests/{requestId:guid}/accept", FriendEndpoints.AcceptRequestAsync)
            .WithName("AcceptFriendRequest")
            .WithSummary("Accepts a request you received.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        friends.MapPost("/requests/{requestId:guid}/decline", FriendEndpoints.DeclineRequestAsync)
            .WithName("DeclineFriendRequest")
            .WithSummary("Declines a request you received. Idempotent.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        friends.MapDelete("/requests/{requestId:guid}", FriendEndpoints.CancelRequestAsync)
            .WithName("CancelFriendRequest")
            .WithSummary("Takes back a request you sent. Idempotent.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var blocks = endpoints.MapGroup("/blocks").WithTags("Friends").RequireAuthorization();

        blocks.MapGet("/", FriendEndpoints.ListBlocksAsync)
            .WithName("ListBlocks")
            .WithSummary("People you blocked, newest first.");

        blocks.MapPut("/{userId:guid}", FriendEndpoints.BlockAsync)
            .WithName("BlockUser")
            .WithSummary("Blocks someone: ends the friendship and cancels pending requests both ways. They cannot send you requests. Idempotent.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        blocks.MapDelete("/{userId:guid}", FriendEndpoints.UnblockAsync)
            .WithName("UnblockUser")
            .WithSummary("Removes a block. Idempotent; the friendship is not restored.");

        return endpoints;
    }

    public static Task MigrateSocialDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        services.MigrateModuleAsync<SocialDbContext>(cancellationToken);
}

internal sealed class SocialModuleApi(SocialDbContext db) : ISocialModule
{
    public async Task<IReadOnlyList<Guid>> ListFriendIdsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.Friendships.AsNoTracking()
            .Where(f => f.UserLowId == userId || f.UserHighId == userId)
            .Select(f => f.UserLowId == userId ? f.UserHighId : f.UserLowId)
            .ToListAsync(cancellationToken);

    public Task<bool> IsBlockedEitherWayAsync(Guid userId, Guid otherUserId, CancellationToken cancellationToken = default) =>
        db.Blocks.AnyAsync(
            b => (b.BlockerId == userId && b.BlockedId == otherUserId) || (b.BlockerId == otherUserId && b.BlockedId == userId),
            cancellationToken);
}
