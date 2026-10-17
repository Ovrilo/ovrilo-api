using Microsoft.EntityFrameworkCore;

using Ovrilo.Modules.Messaging.Persistence;

namespace Ovrilo.Modules.Messaging.Features;

/// <summary>Newest message, read position and unread count of one conversation for one user.</summary>
internal sealed record ConversationActivity(long? LastMessageId, DateTimeOffset? LastMessageAt, long? LastReadMessageId, int UnreadCount)
{
    /// <summary>
    /// Activity of direct or group conversations for <paramref name="me"/>. Unread counts other
    /// people's messages after the read position and stops at <see cref="ReadStateEndpoints.UnreadCountCap"/>.
    /// </summary>
    public static async Task<Dictionary<Guid, ConversationActivity>> LoadAsync(
        MessagingDbContext db,
        Guid me,
        IReadOnlyCollection<Guid> conversationIds,
        CancellationToken cancellationToken)
    {
        var ids = conversationIds.ToList();
        var reads = await db.ReadStates.AsNoTracking()
            .Where(r => r.UserId == me && ids.Contains(r.ChannelId))
            .ToDictionaryAsync(r => r.ChannelId, r => r.LastReadMessageId, cancellationToken);
        var latest = await db.Messages.AsNoTracking()
            .Where(m => ids.Contains(m.ChannelId) && m.DeletedAt == null)
            .GroupBy(m => m.ChannelId)
            .Select(g => new { ChannelId = g.Key, Id = g.Max(m => m.Id), At = g.Max(m => m.CreatedAt) })
            .ToDictionaryAsync(x => x.ChannelId, cancellationToken);

        var result = new Dictionary<Guid, ConversationActivity>(ids.Count);
        foreach (var id in ids)
        {
            long? read = reads.TryGetValue(id, out var r) ? r : null;
            var last = latest.GetValueOrDefault(id);
            var unread = 0;
            if (last is not null && last.Id > (read ?? 0))
            {
                var after = read ?? 0;
                unread = await db.Messages.AsNoTracking()
                    .Where(m => m.ChannelId == id && m.Id > after && m.DeletedAt == null && m.AuthorId != me)
                    .Take(ReadStateEndpoints.UnreadCountCap)
                    .CountAsync(cancellationToken);
            }

            result[id] = new ConversationActivity(last?.Id, last?.At, read, unread);
        }

        return result;
    }
}
