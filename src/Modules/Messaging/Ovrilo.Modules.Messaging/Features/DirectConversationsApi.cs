using Ovrilo.Modules.Messaging.Contracts;

namespace Ovrilo.Modules.Messaging.Features;

internal sealed class DirectConversationsApi(ConversationAccess access) : IDirectConversations
{
    public async Task<ConversationInfo?> FindAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (await access.FindDirectAsync(conversationId, cancellationToken) is { } dm)
        {
            return new ConversationInfo(dm.Id, false, null, [dm.UserLowId, dm.UserHighId]);
        }

        return await access.FindGroupAsync(conversationId, cancellationToken) is { } group
            ? new ConversationInfo(group.Id, true, group.Name, [.. group.Members.Select(m => m.UserId)])
            : null;
    }

    public Task<bool> CanTalkAsync(Guid userId, Guid otherUserId, CancellationToken cancellationToken = default) =>
        access.CanMessageAsync(userId, otherUserId, cancellationToken);
}
