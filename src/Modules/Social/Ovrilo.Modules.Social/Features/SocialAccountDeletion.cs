using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Social.Persistence;

namespace Ovrilo.Modules.Social.Features;

/// <summary>Friendships, requests and blocks in either direction go; former friends refresh their lists.</summary>
internal sealed class SocialAccountDeletion(SocialDbContext db, IRealtimePublisher realtime) : IAccountDeletionParticipant
{
    public async Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var friends = await db.Friendships
            .Where(f => f.UserLowId == userId || f.UserHighId == userId)
            .Select(f => f.UserLowId == userId ? f.UserHighId : f.UserLowId)
            .ToListAsync(cancellationToken);
        var requesters = await db.FriendRequests
            .Where(r => r.RecipientId == userId || r.RequesterId == userId)
            .Select(r => r.RequesterId == userId ? r.RecipientId : r.RequesterId)
            .ToListAsync(cancellationToken);

        await db.Friendships.Where(f => f.UserLowId == userId || f.UserHighId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.FriendRequests.Where(r => r.RequesterId == userId || r.RecipientId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.Blocks.Where(b => b.BlockerId == userId || b.BlockedId == userId).ExecuteDeleteAsync(cancellationToken);

        var gone = new FriendsChangedEvent(FriendsChange.Updated, new SocialUserResponse(userId, string.Empty, "Deleted user", null));
        foreach (var other in friends.Concat(requesters).Distinct())
        {
            await realtime.PublishToUserAsync(other, SocialEvents.FriendsChanged, gone, cancellationToken);
        }
    }
}
