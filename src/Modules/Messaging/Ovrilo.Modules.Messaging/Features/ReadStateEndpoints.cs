using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Messaging.Persistence;

namespace Ovrilo.Modules.Messaging.Features;

public sealed record MarkReadRequest(long LastReadMessageId);

/// <summary>The caller's read position in a channel. Read positions only move forward.</summary>
public sealed record ReadStateResponse(Guid ChannelId, long LastReadMessageId);

/// <summary>
/// Unread summary of one text channel for the caller. <c>UnreadCount</c> counts other people's messages after
/// <c>LastReadMessageId</c> and stops at <see cref="ReadStateEndpoints.UnreadCountCap"/>. For a channel the caller never
/// read, <c>LastReadMessageId</c> is the newest message from before they joined the community (history is not news), or
/// null when everything is newer.
/// </summary>
public sealed record ChannelUnreadResponse(Guid ChannelId, long? LastReadMessageId, long? LastMessageId, int UnreadCount);

/// <summary>Realtime event names. Payload: <see cref="ReadStateResponse"/>, sent to the reader's own connections.</summary>
public static class ReadStateEvents
{
    public const string ReadStateUpdated = "ReadStateUpdated";
}

internal static class ReadStateEndpoints
{
    /// <summary>Counting stops here; clients show "99+"-style labels from it.</summary>
    public const int UnreadCountCap = 100;

    /// <summary>How many of a never-read channel's newest messages are searched for the last one from before joining.</summary>
    private const int JoinBoundaryScan = 2 * UnreadCountCap;

    public static async Task<Results<Ok<ReadStateResponse>, ValidationProblem, ProblemHttpResult>> MarkReadAsync(
        Guid channelId,
        MarkReadRequest request,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        ConversationAccess access,
        IRealtimePublisher realtime,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        if (!await access.CanReadAsync(channelId, userId, cancellationToken))
        {
            return MessageProblems.ChannelNotFound();
        }

        if (!await db.Messages.AnyAsync(m => m.Id == request.LastReadMessageId && m.ChannelId == channelId, cancellationToken))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["lastReadMessageId"] = ["The message is not in this channel."],
            });
        }

        var (position, moved) = await AdvanceAsync(db, userId, channelId, request.LastReadMessageId, timeProvider, cancellationToken);
        var state = new ReadStateResponse(channelId, position);
        if (moved)
        {
            await realtime.PublishToUserAsync(userId, ReadStateEvents.ReadStateUpdated, state, cancellationToken);
        }

        return TypedResults.Ok(state);
    }

    public static async Task<Results<Ok<List<ChannelUnreadResponse>>, ProblemHttpResult>> ListUnreadAsync(
        Guid communityId,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        IChannelsModule channels,
        ICommunitiesModule communities,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        var channelIds = await channels.ListReadableTextChannelIdsAsync(communityId, userId, cancellationToken);
        var joinedAt = await communities.GetJoinedAtAsync(communityId, userId, cancellationToken);
        if (channelIds is null || joinedAt is null)
        {
            return ApiProblems.Create(StatusCodes.Status404NotFound, "communities.not_found", "Community not found.");
        }

        var readPositions = await db.ReadStates.AsNoTracking()
            .Where(r => r.UserId == userId && channelIds.Contains(r.ChannelId))
            .ToDictionaryAsync(r => r.ChannelId, r => r.LastReadMessageId, cancellationToken);
        var latest = await db.Messages.AsNoTracking()
            .Where(m => channelIds.Contains(m.ChannelId) && m.DeletedAt == null)
            .GroupBy(m => m.ChannelId)
            .Select(g => new { ChannelId = g.Key, LastMessageId = g.Max(m => m.Id) })
            .ToDictionaryAsync(x => x.ChannelId, x => x.LastMessageId, cancellationToken);

        var result = new List<ChannelUnreadResponse>(channelIds.Count);
        foreach (var channelId in channelIds)
        {
            long? read = readPositions.TryGetValue(channelId, out var r) ? r : null;
            long? last = latest.TryGetValue(channelId, out var l) ? l : null;
            if (read is null && last is not null)
            {
                read = await LastMessageBeforeAsync(db, channelId, joinedAt.Value, cancellationToken);
            }
            var count = 0;
            if (last is { } newest && newest > (read ?? 0))
            {
                // One bounded index range scan per channel with something new; communities have few text channels.
                var after = read ?? 0;
                count = await db.Messages.AsNoTracking()
                    .Where(m => m.ChannelId == channelId && m.Id > after && m.DeletedAt == null && m.AuthorId != userId)
                    .Take(UnreadCountCap)
                    .CountAsync(cancellationToken);
            }

            result.Add(new ChannelUnreadResponse(channelId, read, last, count));
        }

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// The newest message posted before <paramref name="joinedAt"/>, among the channel's newest <see cref="JoinBoundaryScan"/>
    /// (one bounded index scan). Not found means at least that many arrived after joining: the count is capped anyway.
    /// </summary>
    private static async Task<long?> LastMessageBeforeAsync(MessagingDbContext db, Guid channelId, DateTimeOffset joinedAt, CancellationToken cancellationToken)
    {
        var newest = await db.Messages.AsNoTracking()
            .Where(m => m.ChannelId == channelId)
            .OrderByDescending(m => m.Id)
            .Take(JoinBoundaryScan)
            .Select(m => new { m.Id, m.CreatedAt })
            .ToListAsync(cancellationToken);
        return newest.FirstOrDefault(m => m.CreatedAt <= joinedAt)?.Id;
    }

    /// <summary>
    /// Moves the read position forward to <paramref name="messageId"/> (never backward) in one
    /// statement, so concurrent devices cannot race it back. Returns the stored position and whether it changed.
    /// </summary>
    public static async Task<(long Position, bool Moved)> AdvanceAsync(
        MessagingDbContext db,
        Guid userId,
        Guid channelId,
        long messageId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetDatabaseUtcNow();
        var moved = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO messaging.channel_read_states (user_id, channel_id, last_read_message_id, updated_at)
            VALUES ({userId}, {channelId}, {messageId}, {now})
            ON CONFLICT (user_id, channel_id) DO UPDATE
                SET last_read_message_id = EXCLUDED.last_read_message_id, updated_at = EXCLUDED.updated_at
                WHERE channel_read_states.last_read_message_id < EXCLUDED.last_read_message_id
            """,
            cancellationToken) > 0;
        var position = await db.ReadStates.AsNoTracking()
            .Where(r => r.UserId == userId && r.ChannelId == channelId)
            .Select(r => r.LastReadMessageId)
            .SingleAsync(cancellationToken);
        return (position, moved);
    }
}
