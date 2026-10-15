using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Communities.Domain;
using Ovrilo.Modules.Communities.Persistence;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Media.Contracts;

namespace Ovrilo.Modules.Communities.Features;

/// <summary>Realtime event names (contracts/events/README.md).</summary>
public static class CommunityEvents
{
    /// <summary>Payload: <see cref="CommunityUpdatedEvent"/>.</summary>
    public const string CommunityUpdated = "CommunityUpdated";

    /// <summary>Payload: <see cref="CommunityDeletedEvent"/>.</summary>
    public const string CommunityDeleted = "CommunityDeleted";
}

internal static class CommunityIconEndpoints
{
    public static async Task<Results<Ok<CommunityResponse>, ValidationProblem, ProblemHttpResult>> SetAsync(
        Guid communityId,
        SetCommunityIconRequest request,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        IMediaModule media,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        var membership = await db.FindActiveMembershipAsync(communityId, userId, cancellationToken, tracking: true);
        if (membership is null)
        {
            return CommunityProblems.NotFound();
        }

        var (community, member) = membership.Value;
        if (!member.Permissions.HasFlag(CommunityPermissions.ManageCommunity))
        {
            return CommunityProblems.PermissionDenied();
        }

        if (community.IconAssetId == request.AssetId)
        {
            return TypedResults.Ok(CommunityEndpoints.ToResponse(community, member.Role));
        }

        var asset = await media.AttachAsync(request.AssetId, userId, MediaPurpose.CommunityIcon, cancellationToken);
        if (asset is null || asset.CommunityId != communityId)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["assetId"] = ["Upload and complete an image for this community's icon first."],
            });
        }

        var previous = community.IconAssetId;
        community.IconAssetId = asset.Id;
        community.UpdatedAt = timeProvider.GetDatabaseUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await AfterChangeAsync(community, previous, media, realtime, cancellationToken);
        return TypedResults.Ok(CommunityEndpoints.ToResponse(community, member.Role));
    }

    public static async Task<Results<Ok<CommunityResponse>, ProblemHttpResult>> RemoveAsync(
        Guid communityId,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        IMediaModule media,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var membership = await db.FindActiveMembershipAsync(communityId, principal.GetRequiredUserId(), cancellationToken, tracking: true);
        if (membership is null)
        {
            return CommunityProblems.NotFound();
        }

        var (community, member) = membership.Value;
        if (!member.Permissions.HasFlag(CommunityPermissions.ManageCommunity))
        {
            return CommunityProblems.PermissionDenied();
        }

        if (community.IconAssetId is { } previous)
        {
            community.IconAssetId = null;
            community.UpdatedAt = timeProvider.GetDatabaseUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            await AfterChangeAsync(community, previous, media, realtime, cancellationToken);
        }

        return TypedResults.Ok(CommunityEndpoints.ToResponse(community, member.Role));
    }

    /// <summary>After the commit: free the replaced image and tell members (ADR-0007: persist, then publish).</summary>
    private static async Task AfterChangeAsync(Community community, Guid? previousIcon, IMediaModule media, IRealtimePublisher realtime, CancellationToken cancellationToken)
    {
        if (previousIcon is { } old)
        {
            await media.ReleaseAsync(old, cancellationToken);
        }

        await realtime.PublishToCommunityAsync(
            community.Id,
            CommunityEvents.CommunityUpdated,
            new CommunityUpdatedEvent(community.Id, community.Name, community.Description, community.IconAssetId),
            cancellationToken);
    }
}
