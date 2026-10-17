using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Messaging.Domain;
using Ovrilo.Modules.Messaging.Persistence;

namespace Ovrilo.Modules.Messaging.Features;

public sealed record OpenDirectMessageRequest(Guid UserId);

/// <summary>The other person in a direct conversation.</summary>
public sealed record ConversationUserResponse(Guid Id, string Username, string DisplayName, Guid? AvatarAssetId);

/// <summary>
/// A direct conversation. Its <c>Id</c> is used as the channel ID with the message and read-state
/// endpoints. <c>UnreadCount</c> counts the other person's messages after <c>LastReadMessageId</c>
/// and stops at 100.
/// </summary>
public sealed record DirectConversationResponse(
    Guid Id,
    ConversationUserResponse User,
    long? LastMessageId,
    DateTimeOffset LastActivityAt,
    long? LastReadMessageId,
    int UnreadCount);

internal static class DirectMessageEndpoints
{
    /// <summary>Opens the conversation with a friend, or returns the existing one (also after unfriending, read only).</summary>
    public static async Task<Results<Created<DirectConversationResponse>, Ok<DirectConversationResponse>, ProblemHttpResult>> OpenAsync(
        OpenDirectMessageRequest request,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        ConversationAccess access,
        IIdentityModule identity,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        if (request.UserId == me)
        {
            return ApiProblems.Create(StatusCodes.Status400BadRequest, "messaging.dm_self", "You cannot message yourself.");
        }

        var other = await identity.GetPublicUserAsync(request.UserId, cancellationToken);
        if (other is null)
        {
            return ApiProblems.Create(StatusCodes.Status404NotFound, "messaging.user_not_found", "User not found.");
        }

        var (low, high) = DirectConversation.Pair(me, other.Id);
        var existing = await FindAsync(db, low, high, cancellationToken);
        if (existing is not null)
        {
            return TypedResults.Ok((await DescribeAsync(db, me, [existing], new Dictionary<Guid, PublicUser> { [other.Id] = other }, cancellationToken))[0]);
        }

        if (!await access.CanMessageAsync(me, other.Id, cancellationToken))
        {
            return ApiProblems.Create(StatusCodes.Status403Forbidden, "messaging.dm_not_allowed", "You can only message your friends.");
        }

        var now = timeProvider.GetDatabaseUtcNow();
        // Insert-if-missing: both people may open the conversation at the same moment.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO messaging.direct_conversations (id, user_low_id, user_high_id, created_at) VALUES ({Guid.CreateVersion7(now)}, {low}, {high}, {now}) ON CONFLICT DO NOTHING",
            cancellationToken);
        var created = (await FindAsync(db, low, high, cancellationToken))!;
        var response = (await DescribeAsync(db, me, [created], new Dictionary<Guid, PublicUser> { [other.Id] = other }, cancellationToken))[0];
        return TypedResults.Created($"/dms/{created.Id}", response);
    }

    /// <summary>Your direct conversations, most recently active first.</summary>
    public static async Task<Ok<List<DirectConversationResponse>>> ListAsync(
        ClaimsPrincipal principal,
        MessagingDbContext db,
        IIdentityModule identity,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var conversations = await db.DirectConversations.AsNoTracking()
            .Where(c => c.UserLowId == me || c.UserHighId == me)
            .ToListAsync(cancellationToken);
        var users = await identity.GetPublicUsersAsync([.. conversations.Select(c => c.Other(me))], cancellationToken);
        var described = await DescribeAsync(db, me, [.. conversations.Where(c => users.ContainsKey(c.Other(me)))], users, cancellationToken);
        return TypedResults.Ok(described.OrderByDescending(c => c.LastActivityAt).ToList());
    }

    private static Task<DirectConversation?> FindAsync(MessagingDbContext db, Guid low, Guid high, CancellationToken cancellationToken) =>
        db.DirectConversations.AsNoTracking().SingleOrDefaultAsync(c => c.UserLowId == low && c.UserHighId == high, cancellationToken);

    private static async Task<List<DirectConversationResponse>> DescribeAsync(
        MessagingDbContext db,
        Guid me,
        List<DirectConversation> conversations,
        IReadOnlyDictionary<Guid, PublicUser> users,
        CancellationToken cancellationToken)
    {
        var ids = conversations.Select(c => c.Id).ToList();
        var reads = await db.ReadStates.AsNoTracking()
            .Where(r => r.UserId == me && ids.Contains(r.ChannelId))
            .ToDictionaryAsync(r => r.ChannelId, r => r.LastReadMessageId, cancellationToken);
        var latest = await db.Messages.AsNoTracking()
            .Where(m => ids.Contains(m.ChannelId) && m.DeletedAt == null)
            .GroupBy(m => m.ChannelId)
            .Select(g => new { ChannelId = g.Key, Id = g.Max(m => m.Id), At = g.Max(m => m.CreatedAt) })
            .ToDictionaryAsync(x => x.ChannelId, cancellationToken);

        var result = new List<DirectConversationResponse>(conversations.Count);
        foreach (var conversation in conversations)
        {
            long? read = reads.TryGetValue(conversation.Id, out var r) ? r : null;
            var last = latest.GetValueOrDefault(conversation.Id);
            var unread = 0;
            if (last is not null && last.Id > (read ?? 0))
            {
                var after = read ?? 0;
                unread = await db.Messages.AsNoTracking()
                    .Where(m => m.ChannelId == conversation.Id && m.Id > after && m.DeletedAt == null && m.AuthorId != me)
                    .Take(ReadStateEndpoints.UnreadCountCap)
                    .CountAsync(cancellationToken);
            }

            var user = users[conversation.Other(me)];
            result.Add(new DirectConversationResponse(
                conversation.Id,
                new ConversationUserResponse(user.Id, user.Username, user.DisplayName, user.AvatarAssetId),
                last?.Id,
                last?.At ?? conversation.CreatedAt,
                read,
                unread));
        }

        return result;
    }
}
