using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Social.Domain;
using Ovrilo.Modules.Social.Persistence;

namespace Ovrilo.Modules.Social.Features;

internal static class FriendEndpoints
{
    public static async Task<Ok<List<FriendResponse>>> ListFriendsAsync(
        ClaimsPrincipal principal,
        SocialDbContext db,
        IIdentityModule identity,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var friendships = await db.Friendships.AsNoTracking()
            .Where(f => f.UserLowId == me || f.UserHighId == me)
            .ToListAsync(cancellationToken);
        var users = await identity.GetPublicUsersAsync([.. friendships.Select(f => f.Other(me))], cancellationToken);
        return TypedResults.Ok(friendships
            .Where(f => users.ContainsKey(f.Other(me)))
            .Select(f => new FriendResponse(SocialUserResponse.From(users[f.Other(me)]), f.CreatedAt))
            .OrderBy(f => f.User.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    public static async Task<Ok<FriendRequestListResponse>> ListRequestsAsync(
        ClaimsPrincipal principal,
        SocialDbContext db,
        IIdentityModule identity,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var pending = await db.FriendRequests.AsNoTracking()
            .Where(r => r.Status == FriendRequestStatus.Pending && (r.RecipientId == me || r.RequesterId == me))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        Guid Other(FriendRequest r) => r.RecipientId == me ? r.RequesterId : r.RecipientId;
        var users = await identity.GetPublicUsersAsync([.. pending.Select(Other)], cancellationToken);
        List<FriendRequestResponse> Select(Func<FriendRequest, bool> which) =>
        [
            .. pending.Where(which)
                .Where(r => users.ContainsKey(Other(r)))
                .Select(r => new FriendRequestResponse(r.Id, SocialUserResponse.From(users[Other(r)]), r.CreatedAt)),
        ];
        return TypedResults.Ok(new FriendRequestListResponse(Select(r => r.RecipientId == me), Select(r => r.RequesterId == me)));
    }

    /// <summary>
    /// Sends a request by username. Asking someone who already asked you makes you friends; asking
    /// again while a request is pending returns that request.
    /// </summary>
    public static async Task<Results<Created<SendFriendRequestResponse>, Ok<SendFriendRequestResponse>, ProblemHttpResult>> SendRequestAsync(
        SendFriendRequestRequest request,
        ClaimsPrincipal principal,
        SocialDbContext db,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var target = string.IsNullOrWhiteSpace(request.Username)
            ? null
            : await identity.FindByUsernameAsync(request.Username, cancellationToken);
        if (target is null)
        {
            return SocialProblems.UserNotFound();
        }

        if (target.Id == me)
        {
            return SocialProblems.CannotAddSelf();
        }

        if (await BlockedEitherWayAsync(db, me, target.Id, cancellationToken))
        {
            return SocialProblems.UserNotFound();
        }

        if (await AreFriendsAsync(db, me, target.Id, cancellationToken))
        {
            return SocialProblems.AlreadyFriends();
        }

        var self = await identity.GetPublicUserAsync(me, cancellationToken);
        var now = timeProvider.GetDatabaseUtcNow();

        // They already asked: accept their request instead of sending another.
        var theirs = await db.FriendRequests.SingleOrDefaultAsync(
            r => r.RequesterId == target.Id && r.RecipientId == me && r.Status == FriendRequestStatus.Pending, cancellationToken);
        if (theirs is not null)
        {
            var since = await AcceptAsync(db, theirs.Id, me, target.Id, now, cancellationToken);
            await NotifyFriendAddedAsync(realtime, me, target, self, cancellationToken);
            return TypedResults.Ok(new SendFriendRequestResponse(
                FriendRequestOutcome.Friends, null, new FriendResponse(SocialUserResponse.From(target), since)));
        }

        var existing = await FindPendingAsync(db, me, target.Id, cancellationToken);
        if (existing is not null)
        {
            return TypedResults.Ok(Requested(existing, target));
        }

        var created = new FriendRequest
        {
            Id = Guid.CreateVersion7(now),
            RequesterId = me,
            RecipientId = target.Id,
            Status = FriendRequestStatus.Pending,
            CreatedAt = now,
        };
        db.FriendRequests.Add(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(SocialDbContext.UniquePendingRequestIndex))
        {
            // A concurrent send created it first.
            db.ChangeTracker.Clear();
            var winner = await FindPendingAsync(db, me, target.Id, cancellationToken);
            return winner is null ? SocialProblems.RequestNotPending() : TypedResults.Ok(Requested(winner, target));
        }

        if (self is not null)
        {
            await NotifyAsync(realtime, target.Id, FriendsChange.RequestReceived, self, cancellationToken);
        }

        await NotifyAsync(realtime, me, FriendsChange.Updated, target, cancellationToken);
        return TypedResults.Created("/friends/requests", Requested(created, target));
    }

    public static async Task<Results<Ok<FriendResponse>, ProblemHttpResult>> AcceptRequestAsync(
        Guid requestId,
        ClaimsPrincipal principal,
        SocialDbContext db,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var request = await db.FriendRequests.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == requestId && r.RecipientId == me, cancellationToken);
        var requester = request is null ? null : await identity.GetPublicUserAsync(request.RequesterId, cancellationToken);
        if (request is null || requester is null || await BlockedEitherWayAsync(db, me, request.RequesterId, cancellationToken))
        {
            return SocialProblems.RequestNotFound();
        }

        if (request.Status == FriendRequestStatus.Accepted)
        {
            var friendship = await FindFriendshipAsync(db, me, requester.Id, cancellationToken);
            return friendship is null
                ? SocialProblems.RequestNotPending()
                : TypedResults.Ok(new FriendResponse(SocialUserResponse.From(requester), friendship.CreatedAt));
        }

        if (request.Status != FriendRequestStatus.Pending)
        {
            return SocialProblems.RequestNotPending();
        }

        var since = await AcceptAsync(db, request.Id, me, requester.Id, timeProvider.GetDatabaseUtcNow(), cancellationToken);
        await NotifyFriendAddedAsync(realtime, me, requester, await identity.GetPublicUserAsync(me, cancellationToken), cancellationToken);
        return TypedResults.Ok(new FriendResponse(SocialUserResponse.From(requester), since));
    }

    /// <summary>The recipient declines. Idempotent.</summary>
    public static Task<Results<NoContent, ProblemHttpResult>> DeclineRequestAsync(
        Guid requestId,
        ClaimsPrincipal principal,
        SocialDbContext db,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ResolveAsync(requestId, principal.GetRequiredUserId(), asRecipient: true, FriendRequestStatus.Declined, db, identity, realtime, timeProvider, cancellationToken);

    /// <summary>The sender takes the request back. Idempotent.</summary>
    public static Task<Results<NoContent, ProblemHttpResult>> CancelRequestAsync(
        Guid requestId,
        ClaimsPrincipal principal,
        SocialDbContext db,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ResolveAsync(requestId, principal.GetRequiredUserId(), asRecipient: false, FriendRequestStatus.Cancelled, db, identity, realtime, timeProvider, cancellationToken);

    /// <summary>Ends a friendship. Idempotent.</summary>
    public static async Task<NoContent> RemoveFriendAsync(
        Guid userId,
        ClaimsPrincipal principal,
        SocialDbContext db,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var (low, high) = Friendship.Pair(me, userId);
        var removed = await db.Friendships.Where(f => f.UserLowId == low && f.UserHighId == high).ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            await NotifyBothUpdatedAsync(realtime, identity, me, userId, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    public static async Task<Ok<List<BlockedUserResponse>>> ListBlocksAsync(
        ClaimsPrincipal principal,
        SocialDbContext db,
        IIdentityModule identity,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var blocks = await db.Blocks.AsNoTracking()
            .Where(b => b.BlockerId == me)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken);
        var users = await identity.GetPublicUsersAsync([.. blocks.Select(b => b.BlockedId)], cancellationToken);
        return TypedResults.Ok(blocks
            .Where(b => users.ContainsKey(b.BlockedId))
            .Select(b => new BlockedUserResponse(SocialUserResponse.From(users[b.BlockedId]), b.CreatedAt))
            .ToList());
    }

    /// <summary>Blocks someone: ends the friendship and cancels pending requests both ways. Idempotent.</summary>
    public static async Task<Results<NoContent, ProblemHttpResult>> BlockAsync(
        Guid userId,
        ClaimsPrincipal principal,
        SocialDbContext db,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        if (userId == me)
        {
            return SocialProblems.CannotBlockSelf();
        }

        if (!await identity.IsAccountActiveAsync(userId, cancellationToken))
        {
            return SocialProblems.UserNotFound();
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var (low, high) = Friendship.Pair(me, userId);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Insert-if-missing: a failed statement would abort the whole transaction.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO social.blocks (blocker_id, blocked_id, created_at) VALUES ({me}, {userId}, {now}) ON CONFLICT DO NOTHING",
                cancellationToken);
            await db.Friendships.Where(f => f.UserLowId == low && f.UserHighId == high).ExecuteDeleteAsync(cancellationToken);
            await db.FriendRequests
                .Where(r => r.Status == FriendRequestStatus.Pending
                    && ((r.RequesterId == me && r.RecipientId == userId) || (r.RequesterId == userId && r.RecipientId == me)))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.Status, FriendRequestStatus.Cancelled)
                    .SetProperty(r => r.ResolvedAt, now), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await NotifyBothUpdatedAsync(realtime, identity, me, userId, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Removes a block. Idempotent; does not restore the friendship.</summary>
    public static async Task<NoContent> UnblockAsync(
        Guid userId,
        ClaimsPrincipal principal,
        SocialDbContext db,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var removed = await db.Blocks.Where(b => b.BlockerId == me && b.BlockedId == userId).ExecuteDeleteAsync(cancellationToken);
        if (removed > 0 && await identity.GetPublicUserAsync(userId, cancellationToken) is { } user)
        {
            await NotifyAsync(realtime, me, FriendsChange.Updated, user, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ResolveAsync(
        Guid requestId,
        Guid me,
        bool asRecipient,
        FriendRequestStatus resolution,
        SocialDbContext db,
        IIdentityModule identity,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var request = await db.FriendRequests.AsNoTracking().SingleOrDefaultAsync(
            r => r.Id == requestId && (asRecipient ? r.RecipientId == me : r.RequesterId == me), cancellationToken);
        if (request is null)
        {
            return SocialProblems.RequestNotFound();
        }

        if (request.Status == resolution)
        {
            return TypedResults.NoContent();
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var resolved = await db.FriendRequests
            .Where(r => r.Id == requestId && r.Status == FriendRequestStatus.Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, resolution).SetProperty(r => r.ResolvedAt, now), cancellationToken);
        if (resolved == 0)
        {
            return SocialProblems.RequestNotPending();
        }

        await NotifyBothUpdatedAsync(realtime, identity, request.RequesterId, request.RecipientId, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Marks a pending request accepted and creates the friendship in one transaction; returns when it began.</summary>
    private static async Task<DateTimeOffset> AcceptAsync(
        SocialDbContext db,
        Guid requestId,
        Guid me,
        Guid other,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (low, high) = Friendship.Pair(me, other);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.FriendRequests
            .Where(r => r.Id == requestId && r.Status == FriendRequestStatus.Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, FriendRequestStatus.Accepted).SetProperty(r => r.ResolvedAt, now), cancellationToken);
        // A request the other way may also be pending (sent at the same time): it is answered too.
        await db.FriendRequests
            .Where(r => r.RequesterId == me && r.RecipientId == other && r.Status == FriendRequestStatus.Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, FriendRequestStatus.Accepted).SetProperty(r => r.ResolvedAt, now), cancellationToken);
        // Insert-if-missing in one statement, so concurrent accepts cannot conflict.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO social.friendships (user_low_id, user_high_id, created_at) VALUES ({low}, {high}, {now}) ON CONFLICT DO NOTHING",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await FindFriendshipAsync(db, me, other, cancellationToken))?.CreatedAt ?? now;
    }

    private static Task<bool> BlockedEitherWayAsync(SocialDbContext db, Guid a, Guid b, CancellationToken cancellationToken) =>
        db.Blocks.AnyAsync(x => (x.BlockerId == a && x.BlockedId == b) || (x.BlockerId == b && x.BlockedId == a), cancellationToken);

    private static Task<bool> AreFriendsAsync(SocialDbContext db, Guid a, Guid b, CancellationToken cancellationToken)
    {
        var (low, high) = Friendship.Pair(a, b);
        return db.Friendships.AnyAsync(f => f.UserLowId == low && f.UserHighId == high, cancellationToken);
    }

    private static Task<Friendship?> FindFriendshipAsync(SocialDbContext db, Guid a, Guid b, CancellationToken cancellationToken)
    {
        var (low, high) = Friendship.Pair(a, b);
        return db.Friendships.AsNoTracking().SingleOrDefaultAsync(f => f.UserLowId == low && f.UserHighId == high, cancellationToken);
    }

    private static Task<FriendRequest?> FindPendingAsync(SocialDbContext db, Guid requester, Guid recipient, CancellationToken cancellationToken) =>
        db.FriendRequests.AsNoTracking().SingleOrDefaultAsync(
            r => r.RequesterId == requester && r.RecipientId == recipient && r.Status == FriendRequestStatus.Pending, cancellationToken);

    private static SendFriendRequestResponse Requested(FriendRequest request, PublicUser recipient) =>
        new(FriendRequestOutcome.Requested, new FriendRequestResponse(request.Id, SocialUserResponse.From(recipient), request.CreatedAt), null);

    private static Task NotifyAsync(IRealtimePublisher realtime, Guid userId, FriendsChange change, PublicUser other, CancellationToken cancellationToken) =>
        realtime.PublishToUserAsync(userId, SocialEvents.FriendsChanged, new FriendsChangedEvent(change, SocialUserResponse.From(other)), cancellationToken);

    private static async Task NotifyFriendAddedAsync(IRealtimePublisher realtime, Guid me, PublicUser other, PublicUser? self, CancellationToken cancellationToken)
    {
        await NotifyAsync(realtime, me, FriendsChange.FriendAdded, other, cancellationToken);
        if (self is not null)
        {
            await NotifyAsync(realtime, other.Id, FriendsChange.FriendAdded, self, cancellationToken);
        }
    }

    private static async Task NotifyBothUpdatedAsync(IRealtimePublisher realtime, IIdentityModule identity, Guid a, Guid b, CancellationToken cancellationToken)
    {
        var users = await identity.GetPublicUsersAsync([a, b], cancellationToken);
        if (users.TryGetValue(b, out var userB))
        {
            await NotifyAsync(realtime, a, FriendsChange.Updated, userB, cancellationToken);
        }

        if (users.TryGetValue(a, out var userA))
        {
            await NotifyAsync(realtime, b, FriendsChange.Updated, userA, cancellationToken);
        }
    }
}
