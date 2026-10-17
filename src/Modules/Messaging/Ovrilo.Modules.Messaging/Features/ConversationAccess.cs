using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Messaging.Domain;
using Ovrilo.Modules.Messaging.Persistence;
using Ovrilo.Modules.Social.Contracts;

namespace Ovrilo.Modules.Messaging.Features;

/// <summary>
/// Who may read, write and moderate a conversation, and where its events go. A conversation is a
/// community text channel (rules from Channels) or a direct conversation (rules here: participants
/// read; friends without a block write; nobody moderates the other person).
/// </summary>
internal sealed class ConversationAccess(
    MessagingDbContext db,
    IChannelsModule channels,
    ISocialModule social,
    IRealtimePublisher realtime)
{
    private readonly Dictionary<Guid, DirectConversation?> direct = [];

    public async Task<DirectConversation?> FindDirectAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        if (!direct.TryGetValue(conversationId, out var conversation))
        {
            conversation = await db.DirectConversations.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
            direct[conversationId] = conversation;
        }

        return conversation;
    }

    public async Task<bool> CanReadAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        await FindDirectAsync(conversationId, cancellationToken) is { } dm
            ? dm.Includes(userId)
            : await channels.CanReadChannelAsync(conversationId, userId, cancellationToken);

    public async Task<bool> CanWriteAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        if (await FindDirectAsync(conversationId, cancellationToken) is not { } dm)
        {
            return await channels.CanWriteMessagesAsync(conversationId, userId, cancellationToken);
        }

        return dm.Includes(userId) && await CanMessageAsync(userId, dm.Other(userId), cancellationToken);
    }

    public async Task<bool> CanManageAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        await FindDirectAsync(conversationId, cancellationToken) is null
        && await channels.CanManageMessagesAsync(conversationId, userId, cancellationToken);

    /// <summary>Friends without a block either way may start and continue direct conversations.</summary>
    public async Task<bool> CanMessageAsync(Guid userId, Guid otherUserId, CancellationToken cancellationToken) =>
        await social.AreFriendsAsync(userId, otherUserId, cancellationToken)
        && !await social.IsBlockedEitherWayAsync(userId, otherUserId, cancellationToken);

    /// <summary>
    /// Channel events go to the channel's subscribers; direct-conversation events go to both
    /// participants' own connections, so unread counts update without opening the conversation.
    /// </summary>
    public async Task PublishAsync(Guid conversationId, string eventName, object payload, CancellationToken cancellationToken)
    {
        if (await FindDirectAsync(conversationId, cancellationToken) is { } dm)
        {
            await realtime.PublishToUserAsync(dm.UserLowId, eventName, payload, cancellationToken);
            await realtime.PublishToUserAsync(dm.UserHighId, eventName, payload, cancellationToken);
        }
        else
        {
            await realtime.PublishToChannelAsync(conversationId, eventName, payload, cancellationToken);
        }
    }
}
