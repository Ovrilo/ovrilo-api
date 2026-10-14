using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Identity.Domain;
using Ovrilo.Modules.Identity.Persistence;
using Ovrilo.Modules.Media.Contracts;

namespace Ovrilo.Modules.Identity.Features;

/// <summary>Realtime event names (contracts/events/README.md).</summary>
public static class IdentityEvents
{
    /// <summary>Payload: <see cref="UserUpdatedEvent"/>.</summary>
    public const string UserUpdated = "UserUpdated";
}

internal static class Avatar
{
    public static async Task<Results<Ok<CurrentUserResponse>, ValidationProblem, UnauthorizedHttpResult>> SetAsync(
        SetAvatarRequest request,
        ClaimsPrincipal principal,
        IdentityDbContext db,
        IMediaModule media,
        ICommunitiesModule communities,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await FindActiveUserAsync(principal, db, cancellationToken);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (user.AvatarAssetId == request.AssetId)
        {
            return TypedResults.Ok(SessionIssuer.ToCurrentUser(user));
        }

        if (await media.AttachAsync(request.AssetId, user.Id, MediaPurpose.Avatar, cancellationToken) is null)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["assetId"] = ["Upload and complete an avatar image first."],
            });
        }

        var previous = user.AvatarAssetId;
        user.AvatarAssetId = request.AssetId;
        user.UpdatedAt = timeProvider.GetDatabaseUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await AfterChangeAsync(user, previous, media, communities, realtime, cancellationToken);
        return TypedResults.Ok(SessionIssuer.ToCurrentUser(user));
    }

    public static async Task<Results<Ok<CurrentUserResponse>, UnauthorizedHttpResult>> RemoveAsync(
        ClaimsPrincipal principal,
        IdentityDbContext db,
        IMediaModule media,
        ICommunitiesModule communities,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await FindActiveUserAsync(principal, db, cancellationToken);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (user.AvatarAssetId is { } previous)
        {
            user.AvatarAssetId = null;
            user.UpdatedAt = timeProvider.GetDatabaseUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            await AfterChangeAsync(user, previous, media, communities, realtime, cancellationToken);
        }

        return TypedResults.Ok(SessionIssuer.ToCurrentUser(user));
    }

    private static Task<User?> FindActiveUserAsync(ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken) =>
        principal.GetUserId() is { } userId
            ? db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken)
            : Task.FromResult<User?>(null);

    /// <summary>After the commit: free the replaced image and tell everyone who can see this user (ADR-0007).</summary>
    private static async Task AfterChangeAsync(
        User user,
        Guid? previousAvatar,
        IMediaModule media,
        ICommunitiesModule communities,
        IRealtimePublisher realtime,
        CancellationToken cancellationToken)
    {
        if (previousAvatar is { } old)
        {
            await media.ReleaseAsync(old, cancellationToken);
        }

        var payload = new UserUpdatedEvent(user.Id, user.DisplayName, user.AvatarAssetId);
        foreach (var communityId in await communities.ListCommunityIdsAsync(user.Id, cancellationToken))
        {
            await realtime.PublishToCommunityAsync(communityId, IdentityEvents.UserUpdated, payload, cancellationToken);
        }

        await realtime.PublishToUserAsync(user.Id, IdentityEvents.UserUpdated, payload, cancellationToken);
    }
}
