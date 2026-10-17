using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Messaging.Domain;
using Ovrilo.Modules.Messaging.Persistence;
using Ovrilo.Modules.Social.Contracts;

namespace Ovrilo.Modules.Messaging.Features;

/// <summary>
/// Who may read, write and moderate a conversation, and where its events go. A conversation is a
/// community text channel (rules from Channels), a direct conversation (participants read; friends
/// without a block write) or a group (members read and write). Nobody moderates other people's
/// messages outside communities.
/// </summary>
internal sealed class ConversationAccess(
    MessagingDbContext db,
    IChannelsModule channels,
    ISocialModule social,
    IRealtimePublisher realtime)
{
    private readonly Dictionary<Guid, DirectConversation?> direct = [];
    private readonly Dictionary<Guid, GroupConversation?> groups = [];

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

    public async Task<GroupConversation?> FindGroupAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        if (!groups.TryGetValue(conversationId, out var group))
        {
            group = await db.GroupConversations.AsNoTracking()
                .Include(g => g.Members)
                .SingleOrDefaultAsync(g => g.Id == conversationId, cancellationToken);
            groups[conversationId] = group;
        }

        return group;
    }

    /// <summary>The people a direct or group conversation belongs to; null for community channels.</summary>
    public async Task<IReadOnlyList<Guid>?> FindMembersAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        if (await FindDirectAsync(conversationId, cancellationToken) is { } dm)
        {
            return [dm.UserLowId, dm.UserHighId];
        }

        return await FindGroupAsync(conversationId, cancellationToken) is { } group
            ? [.. group.Members.Select(m => m.UserId)]
            : null;
    }

    public async Task<bool> CanReadAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        await FindMembersAsync(conversationId, cancellationToken) is { } members
            ? members.Contains(userId)
            : await channels.CanReadChannelAsync(conversationId, userId, cancellationToken);

    public async Task<bool> CanWriteAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        if (await FindDirectAsync(conversationId, cancellationToken) is { } dm)
        {
            return dm.Includes(userId) && await CanMessageAsync(userId, dm.Other(userId), cancellationToken);
        }

        if (await FindGroupAsync(conversationId, cancellationToken) is { } group)
        {
            return group.Includes(userId);
        }

        return await channels.CanWriteMessagesAsync(conversationId, userId, cancellationToken);
    }

    public async Task<bool> CanManageAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        await FindMembersAsync(conversationId, cancellationToken) is null
        && await channels.CanManageMessagesAsync(conversationId, userId, cancellationToken);

    /// <summary>Friends without a block either way may start and continue direct conversations, and add each other to groups.</summary>
    public async Task<bool> CanMessageAsync(Guid userId, Guid otherUserId, CancellationToken cancellationToken) =>
        await social.AreFriendsAsync(userId, otherUserId, cancellationToken)
        && !await social.IsBlockedEitherWayAsync(userId, otherUserId, cancellationToken);

    /// <summary>
    /// Channel events go to the channel's subscribers; direct and group conversation events go to
    /// each member's own connections, so unread counts update without opening the conversation.
    /// </summary>
    public async Task PublishAsync(Guid conversationId, string eventName, object payload, CancellationToken cancellationToken)
    {
        if (await FindMembersAsync(conversationId, cancellationToken) is { } members)
        {
            foreach (var member in members)
            {
                await realtime.PublishToUserAsync(member, eventName, payload, cancellationToken);
            }
        }
        else
        {
            await realtime.PublishToChannelAsync(conversationId, eventName, payload, cancellationToken);
        }
    }
}
