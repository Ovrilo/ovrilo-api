using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Messaging.Domain;
using Ovrilo.Modules.Messaging.Persistence;

namespace Ovrilo.Modules.Messaging.Features;

/// <param name="UserIds">Friends to start the group with (1 to 9, not yourself).</param>
/// <param name="Name">Optional; empty or missing shows the members' names.</param>
public sealed record CreateGroupRequest(IReadOnlyList<Guid> UserIds, string? Name);

public sealed record RenameGroupRequest(string? Name);

public sealed record AddGroupMemberRequest(Guid UserId);

/// <summary>
/// A group conversation. Its <c>Id</c> is used as the channel ID with the message, read-state and
/// call endpoints. <c>Members</c> includes the caller, oldest member first.
/// </summary>
public sealed record GroupConversationResponse(
    Guid Id,
    string? Name,
    Guid OwnerId,
    IReadOnlyList<ConversationUserResponse> Members,
    long? LastMessageId,
    DateTimeOffset LastActivityAt,
    long? LastReadMessageId,
    int UnreadCount);

public enum GroupChange
{
    /// <summary>You were added to the group (or created it).</summary>
    Added,

    /// <summary>Name, owner or members changed.</summary>
    Updated,

    /// <summary>You left or were removed; the group is gone from your list.</summary>
    Removed,
}

/// <summary>Sent to each member's own connections; clients refetch <c>GET /groups</c>.</summary>
public sealed record GroupChangedEvent(GroupChange Change, Guid GroupId);

public static class GroupEvents
{
    public const string GroupChanged = nameof(GroupChanged);
}

internal static class GroupEndpoints
{
    public static async Task<Results<Created<GroupConversationResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateGroupRequest request,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        ConversationAccess access,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var others = (request.UserIds ?? []).Where(id => id != me).Distinct().ToList();
        if (others.Count is 0 or >= GroupConversation.MaxMembers)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["userIds"] = [$"Pick between 1 and {GroupConversation.MaxMembers - 1} friends."],
            });
        }

        if (NameProblem(request.Name) is { } invalid)
        {
            return invalid;
        }

        var users = await identity.GetPublicUsersAsync([me, .. others], cancellationToken);
        foreach (var other in others)
        {
            if (!users.ContainsKey(other) || !await access.CanMessageAsync(me, other, cancellationToken))
            {
                return NotAllowed();
            }
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var group = new GroupConversation
        {
            Id = Guid.CreateVersion7(now),
            Name = Clean(request.Name),
            OwnerId = me,
            CreatedAt = now,
        };
        // The creator joins first so they are the oldest member (PostgreSQL keeps microseconds).
        group.Members.Add(new GroupMember { ConversationId = group.Id, UserId = me, JoinedAt = now });
        var joined = now.AddMicroseconds(1);
        group.Members.AddRange(others.Select(id => new GroupMember { ConversationId = group.Id, UserId = id, JoinedAt = joined }));
        db.GroupConversations.Add(group);
        await db.SaveChangesAsync(cancellationToken);

        await NotifyAsync(realtime, group.Members.Select(m => m.UserId), GroupChange.Added, group.Id, cancellationToken);
        var response = (await DescribeAsync(db, identity, me, [group], cancellationToken))[0];
        return TypedResults.Created($"/groups/{group.Id}", response);
    }

    /// <summary>Your groups, most recently active first.</summary>
    public static async Task<Ok<List<GroupConversationResponse>>> ListAsync(
        ClaimsPrincipal principal,
        MessagingDbContext db,
        IIdentityModule identity,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var groups = await db.GroupConversations.AsNoTracking()
            .Include(g => g.Members)
            .Where(g => g.Members.Any(m => m.UserId == me))
            .ToListAsync(cancellationToken);
        var described = await DescribeAsync(db, identity, me, groups, cancellationToken);
        return TypedResults.Ok(described.OrderByDescending(g => g.LastActivityAt).ToList());
    }

    /// <summary>Any member may rename the group; an empty name clears it.</summary>
    public static async Task<Results<Ok<GroupConversationResponse>, ValidationProblem, ProblemHttpResult>> RenameAsync(
        Guid groupId,
        RenameGroupRequest request,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        if (NameProblem(request.Name) is { } invalid)
        {
            return invalid;
        }

        var group = await db.GroupConversations.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null || !group.Includes(me))
        {
            return NotFound();
        }

        group.Name = Clean(request.Name);
        await db.SaveChangesAsync(cancellationToken);
        await NotifyAsync(realtime, group.Members.Select(m => m.UserId), GroupChange.Updated, group.Id, cancellationToken);
        return TypedResults.Ok((await DescribeAsync(db, identity, me, [group], cancellationToken))[0]);
    }

    /// <summary>A member adds one of their friends. Adding someone who is already a member changes nothing.</summary>
    public static async Task<Results<Ok<GroupConversationResponse>, ProblemHttpResult>> AddMemberAsync(
        Guid groupId,
        AddGroupMemberRequest request,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        ConversationAccess access,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var group = await LockAsync(db, groupId, cancellationToken);
        if (group is null || !group.Includes(me))
        {
            return NotFound();
        }

        if (group.Includes(request.UserId))
        {
            return TypedResults.Ok((await DescribeAsync(db, identity, me, [group], cancellationToken))[0]);
        }

        if (await identity.GetPublicUserAsync(request.UserId, cancellationToken) is null
            || !await access.CanMessageAsync(me, request.UserId, cancellationToken))
        {
            return NotAllowed();
        }

        if (group.Members.Count >= GroupConversation.MaxMembers)
        {
            return ApiProblems.Create(StatusCodes.Status409Conflict, "messaging.group_full", $"A group can have up to {GroupConversation.MaxMembers} people.");
        }

        var now = timeProvider.GetDatabaseUtcNow();
        group.Members.Add(new GroupMember { ConversationId = group.Id, UserId = request.UserId, JoinedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        // Earlier history is readable but not unread for the new member.
        var newest = await db.Messages.Where(m => m.ChannelId == group.Id).MaxAsync(m => (long?)m.Id, cancellationToken);
        if (newest is { } position)
        {
            await ReadStateEndpoints.AdvanceAsync(db, request.UserId, group.Id, position, timeProvider, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        await NotifyAsync(realtime, [request.UserId], GroupChange.Added, group.Id, cancellationToken);
        await NotifyAsync(realtime, group.Members.Select(m => m.UserId).Where(id => id != request.UserId), GroupChange.Updated, group.Id, cancellationToken);
        return TypedResults.Ok((await DescribeAsync(db, identity, me, [group], cancellationToken))[0]);
    }

    /// <summary>
    /// Leave the group (your own ID), or remove someone as its owner. When the owner leaves, the
    /// longest-standing member becomes the owner. Idempotent for people who are not members.
    /// </summary>
    public static async Task<Results<NoContent, ProblemHttpResult>> RemoveMemberAsync(
        Guid groupId,
        Guid userId,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        IRealtimePublisher realtime,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var group = await LockAsync(db, groupId, cancellationToken);
        if (group is null || !group.Includes(me))
        {
            return NotFound();
        }

        if (userId != me && group.OwnerId != me)
        {
            return ApiProblems.Create(StatusCodes.Status403Forbidden, "messaging.group_not_owner", "Only the group owner can remove people.");
        }

        var member = group.Members.Find(m => m.UserId == userId);
        if (member is null)
        {
            return TypedResults.NoContent();
        }

        group.Members.Remove(member);
        if (group.OwnerId == userId && group.Members.Count > 0)
        {
            group.OwnerId = group.Members.OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId).First().UserId;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await NotifyAsync(realtime, [userId], GroupChange.Removed, group.Id, cancellationToken);
        await NotifyAsync(realtime, group.Members.Select(m => m.UserId), GroupChange.Updated, group.Id, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Loads the group with its members, holding a row lock until the transaction ends so membership changes queue up.</summary>
    private static async Task<GroupConversation?> LockAsync(MessagingDbContext db, Guid groupId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM messaging.group_conversations WHERE id = {groupId} FOR UPDATE",
            cancellationToken);
        return await db.GroupConversations.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
    }

    private static async Task<List<GroupConversationResponse>> DescribeAsync(
        MessagingDbContext db,
        IIdentityModule identity,
        Guid me,
        List<GroupConversation> groups,
        CancellationToken cancellationToken)
    {
        var users = await identity.GetPublicUsersAsync([.. groups.SelectMany(g => g.Members).Select(m => m.UserId).Distinct()], cancellationToken);
        var activity = await ConversationActivity.LoadAsync(db, me, [.. groups.Select(g => g.Id)], cancellationToken);
        return
        [
            .. groups.Select(group =>
            {
                var (lastId, lastAt, read, unread) = activity[group.Id];
                var members = group.Members
                    .OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId)
                    .Where(m => users.ContainsKey(m.UserId))
                    .Select(m => ConversationUserResponse.From(users[m.UserId]))
                    .ToList();
                return new GroupConversationResponse(group.Id, group.Name, group.OwnerId, members, lastId, lastAt ?? group.CreatedAt, read, unread);
            }),
        ];
    }

    private static async Task NotifyAsync(IRealtimePublisher realtime, IEnumerable<Guid> userIds, GroupChange change, Guid groupId, CancellationToken cancellationToken)
    {
        var payload = new GroupChangedEvent(change, groupId);
        foreach (var userId in userIds)
        {
            await realtime.PublishToUserAsync(userId, GroupEvents.GroupChanged, payload, cancellationToken);
        }
    }

    private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static ValidationProblem? NameProblem(string? name) =>
        Clean(name) is { Length: > GroupConversation.NameMaxLength }
            ? ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["name"] = [$"Use at most {GroupConversation.NameMaxLength} characters."],
            })
            : null;

    private static ProblemHttpResult NotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "messaging.group_not_found", "Group not found.");

    private static ProblemHttpResult NotAllowed() =>
        ApiProblems.Create(StatusCodes.Status403Forbidden, "messaging.group_not_allowed", "You can only add your friends.");
}
