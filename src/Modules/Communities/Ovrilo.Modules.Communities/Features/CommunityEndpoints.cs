using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Communities.Domain;
using Ovrilo.Modules.Communities.Persistence;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Media.Contracts;

namespace Ovrilo.Modules.Communities.Features;

internal static class CommunityEndpoints
{
    public static async Task<Results<Created<CommunityResponse>, ValidationProblem>> CreateAsync(
        CreateCommunityRequest request,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        IChannelsModule channels,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var name = NormalizeName(request.Name);
        var description = NormalizeDescription(request.Description);

        var errors = new Dictionary<string, string[]>();
        ValidateName(name, errors);
        ValidateDescription(description, errors);
        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        var userId = principal.GetRequiredUserId();
        var now = timeProvider.GetDatabaseUtcNow();
        var community = new Community
        {
            Id = Guid.CreateVersion7(now),
            OwnerId = userId,
            Name = name,
            Description = description,
            Visibility = CommunityVisibility.Private,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Communities.Add(community);
        db.Members.Add(new CommunityMember
        {
            Id = Guid.CreateVersion7(now),
            CommunityId = community.Id,
            UserId = userId,
            Role = CommunityRole.Owner,
            State = MembershipState.Active,
            JoinedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        await channels.CreateDefaultChannelsAsync(community.Id, userId, cancellationToken);

        return TypedResults.Created($"/communities/{community.Id}", ToResponse(community, CommunityRole.Owner));
    }

    public static async Task<Ok<List<CommunityResponse>>> ListMineAsync(
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        var communities = await db.Members.AsNoTracking()
            .Where(m => m.UserId == userId && m.State == MembershipState.Active)
            .Join(db.Communities.Where(c => c.DeletedAt == null), m => m.CommunityId, c => c.Id, (m, c) => new { Community = c, m.Role })
            .OrderBy(x => x.Community.Name)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(communities.Select(x => ToResponse(x.Community, x.Role)).ToList());
    }

    public static async Task<Results<Ok<CommunityResponse>, ProblemHttpResult>> GetAsync(
        Guid communityId,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        CancellationToken cancellationToken)
    {
        var membership = await db.FindActiveMembershipAsync(communityId, principal.GetRequiredUserId(), cancellationToken);
        return membership is null
            ? CommunityProblems.NotFound()
            : TypedResults.Ok(ToResponse(membership.Value.Community, membership.Value.Member.Role));
    }

    public static async Task<Results<Ok<List<MemberResponse>>, ProblemHttpResult>> ListMembersAsync(
        Guid communityId,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        IIdentityModule identity,
        CancellationToken cancellationToken)
    {
        if (await db.FindActiveMembershipAsync(communityId, principal.GetRequiredUserId(), cancellationToken) is null)
        {
            return CommunityProblems.NotFound();
        }

        var members = await db.Members.AsNoTracking()
            .Where(m => m.CommunityId == communityId && m.State == MembershipState.Active)
            .OrderBy(m => m.JoinedAt)
            .ToListAsync(cancellationToken);
        var users = await identity.GetPublicUsersAsync([.. members.Select(m => m.UserId)], cancellationToken);

        return TypedResults.Ok(members
            .Where(m => users.ContainsKey(m.UserId))
            .Select(m => new MemberResponse(m.UserId, users[m.UserId].Username, users[m.UserId].DisplayName, m.Nickname, m.Role, m.JoinedAt, users[m.UserId].AvatarAssetId))
            .ToList());
    }

    public static async Task<Results<NoContent, ProblemHttpResult>> LeaveAsync(
        Guid communityId,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        CancellationToken cancellationToken)
    {
        var membership = await db.FindActiveMembershipAsync(communityId, principal.GetRequiredUserId(), cancellationToken, tracking: true);
        if (membership is null)
        {
            return CommunityProblems.NotFound();
        }

        if (membership.Value.Member.Role == CommunityRole.Owner)
        {
            return CommunityProblems.OwnerCannotLeave();
        }

        membership.Value.Member.State = MembershipState.Left;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Renames the community or changes its description. Needs ManageCommunity; members are told with CommunityUpdated.</summary>
    public static async Task<Results<Ok<CommunityResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid communityId,
        UpdateCommunityRequest request,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
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

        var errors = new Dictionary<string, string[]>();
        if (request.Name is not null)
        {
            var name = NormalizeName(request.Name);
            ValidateName(name, errors);
            community.Name = name;
        }

        if (request.Description is not null)
        {
            var description = NormalizeDescription(request.Description);
            ValidateDescription(description, errors);
            community.Description = description;
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        community.UpdatedAt = timeProvider.GetDatabaseUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await realtime.PublishToCommunityAsync(
            community.Id,
            CommunityEvents.CommunityUpdated,
            new CommunityUpdatedEvent(community.Id, community.Name, community.Description, community.IconAssetId),
            cancellationToken);

        return TypedResults.Ok(ToResponse(community, member.Role));
    }

    /// <summary>
    /// Deletes the community (owner only). It disappears for every member at once: every membership, channel, message
    /// and voice check excludes deleted communities. Rows are kept (soft delete); the icon is released. Members are told
    /// with CommunityDeleted.
    /// </summary>
    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
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
        if (member.Role != CommunityRole.Owner)
        {
            return CommunityProblems.PermissionDenied();
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var icon = community.IconAssetId;
        community.DeletedAt = now;
        community.UpdatedAt = now;
        community.IconAssetId = null;
        await db.SaveChangesAsync(cancellationToken);

        if (icon is { } old)
        {
            await media.ReleaseAsync(old, cancellationToken);
        }

        await realtime.PublishToCommunityAsync(community.Id, CommunityEvents.CommunityDeleted, new CommunityDeletedEvent(community.Id), cancellationToken);
        return TypedResults.NoContent();
    }

    private static string NormalizeName(string? name) => (name ?? string.Empty).Trim();

    private static string? NormalizeDescription(string? description) => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static void ValidateName(string name, Dictionary<string, string[]> errors)
    {
        if (name.Length is 0 or > Community.NameMaxLength || name.Any(char.IsControl))
        {
            errors["name"] = [$"Name must be 1-{Community.NameMaxLength} characters without control characters."];
        }
    }

    private static void ValidateDescription(string? description, Dictionary<string, string[]> errors)
    {
        if (description?.Length > Community.DescriptionMaxLength)
        {
            errors["description"] = [$"Description must be at most {Community.DescriptionMaxLength} characters."];
        }
    }

    public static CommunityResponse ToResponse(Community community, CommunityRole myRole) =>
        new(community.Id, community.Name, community.Description, community.OwnerId, myRole, community.CreatedAt, community.IconAssetId);
}

internal static class MembershipQueries
{
    /// <summary>Returns the community and the caller's membership when the caller is an active member of a live community.</summary>
    public static async Task<(Community Community, CommunityMember Member)?> FindActiveMembershipAsync(
        this CommunitiesDbContext db,
        Guid communityId,
        Guid userId,
        CancellationToken cancellationToken,
        bool tracking = false)
    {
        var members = tracking ? db.Members : db.Members.AsNoTracking();
        var result = await members
            .Where(m => m.CommunityId == communityId && m.UserId == userId && m.State == MembershipState.Active)
            .Join(db.Communities.Where(c => c.DeletedAt == null), m => m.CommunityId, c => c.Id, (m, c) => new { Member = m, Community = c })
            .SingleOrDefaultAsync(cancellationToken);

        return result is null ? null : (result.Community, result.Member);
    }
}
