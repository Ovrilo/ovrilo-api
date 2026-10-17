namespace Ovrilo.Modules.Messaging.Contracts;

/// <summary>The people in a direct (two members) or group conversation.</summary>
public sealed record ConversationInfo(Guid Id, bool IsGroup, string? Name, IReadOnlyList<Guid> MemberIds)
{
    public bool Includes(Guid userId) => MemberIds.Contains(userId);

    public IEnumerable<Guid> Others(Guid userId) => MemberIds.Where(id => id != userId);
}

/// <summary>In-process contract for direct and group conversations, used by Voice Sessions for calls.</summary>
public interface IDirectConversations
{
    Task<ConversationInfo?> FindAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Whether two people may talk now: friends, with no block either way.</summary>
    Task<bool> CanTalkAsync(Guid userId, Guid otherUserId, CancellationToken cancellationToken = default);
}
