namespace Ovrilo.Modules.Social.Domain;

internal enum FriendRequestStatus
{
    Pending,
    Accepted,
    Declined,
    Cancelled,
}

/// <summary>A friend request from one user to another (docs/domain/social.md). Kept after it is resolved.</summary>
internal sealed class FriendRequest
{
    public Guid Id { get; init; }

    public Guid RequesterId { get; init; }

    public Guid RecipientId { get; init; }

    public FriendRequestStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>An undirected friendship, stored once as an ordered pair.</summary>
internal sealed class Friendship
{
    public Guid UserLowId { get; init; }

    public Guid UserHighId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The canonical (low, high) order of two user IDs.</summary>
    public static (Guid Low, Guid High) Pair(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);

    public Guid Other(Guid userId) => userId == UserLowId ? UserHighId : UserLowId;
}

/// <summary>One user blocking another. Directional: blocking does not need the other's consent.</summary>
internal sealed class UserBlock
{
    public Guid BlockerId { get; init; }

    public Guid BlockedId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
