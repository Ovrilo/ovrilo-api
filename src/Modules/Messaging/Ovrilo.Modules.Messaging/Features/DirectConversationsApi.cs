using Microsoft.EntityFrameworkCore;

using Ovrilo.Modules.Messaging.Contracts;
using Ovrilo.Modules.Messaging.Persistence;

namespace Ovrilo.Modules.Messaging.Features;

internal sealed class DirectConversationsApi(MessagingDbContext db, ConversationAccess access) : IDirectConversations
{
    public Task<DirectConversationInfo?> FindAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.DirectConversations.AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new DirectConversationInfo(c.Id, c.UserLowId, c.UserHighId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<bool> CanTalkAsync(Guid userId, Guid otherUserId, CancellationToken cancellationToken = default) =>
        access.CanMessageAsync(userId, otherUserId, cancellationToken);
}
