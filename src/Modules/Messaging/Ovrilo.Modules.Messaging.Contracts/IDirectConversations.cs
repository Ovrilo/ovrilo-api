namespace Ovrilo.Modules.Messaging.Contracts;

/// <summary>The two people in a direct conversation.</summary>
public sealed record DirectConversationInfo(Guid Id, Guid UserLowId, Guid UserHighId)
{
    public bool Includes(Guid userId) => userId == UserLowId || userId == UserHighId;

    public Guid Other(Guid userId) => userId == UserLowId ? UserHighId : UserLowId;
}

/// <summary>In-process contract for direct conversations, used by Voice Sessions for calls.</summary>
public interface IDirectConversations
{
    Task<DirectConversationInfo?> FindAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Whether two people may talk now: friends, with no block either way.</summary>
    Task<bool> CanTalkAsync(Guid userId, Guid otherUserId, CancellationToken cancellationToken = default);
}
