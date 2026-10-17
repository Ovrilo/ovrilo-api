namespace Ovrilo.Modules.Messaging.Domain;

/// <summary>
/// A one-to-one conversation (docs/domain/messaging.md). Its ID is used where a channel ID is used
/// for community messages, so messages, history and read state work the same way.
/// </summary>
internal sealed class DirectConversation
{
    public Guid Id { get; init; }

    /// <summary>The two participants in a fixed order, so a pair has exactly one conversation.</summary>
    public Guid UserLowId { get; init; }

    public Guid UserHighId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public static (Guid Low, Guid High) Pair(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);

    public bool Includes(Guid userId) => userId == UserLowId || userId == UserHighId;

    public Guid Other(Guid userId) => userId == UserLowId ? UserHighId : UserLowId;
}
