namespace Ovrilo.Modules.Social.Contracts;

/// <summary>In-process contract for friendships and blocks (docs/domain/social.md).</summary>
public interface ISocialModule
{
    /// <summary>The user's friends (user IDs; accounts may have been deleted since).</summary>
    Task<IReadOnlyList<Guid>> ListFriendIdsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>True when either user has blocked the other.</summary>
    Task<bool> IsBlockedEitherWayAsync(Guid userId, Guid otherUserId, CancellationToken cancellationToken = default);
}
