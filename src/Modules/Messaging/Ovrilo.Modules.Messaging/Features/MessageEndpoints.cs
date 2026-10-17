using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Messaging.Domain;
using Ovrilo.Modules.Messaging.Persistence;

namespace Ovrilo.Modules.Messaging.Features;

/// <param name="ClientMessageId">Random ID chosen by the client; resending with the same ID returns the original message.</param>
public sealed record SendMessageRequest(Guid ClientMessageId, string Content, long? ReplyToMessageId);

public sealed record EditMessageRequest(string Content);

/// <summary>Display data for a message author. <c>DisplayName</c> and <c>AvatarAssetId</c> are null when the account no longer exists.</summary>
public sealed record MessageAuthor(Guid Id, string? DisplayName, Guid? AvatarAssetId);

/// <summary>A message. Deleted messages are tombstones: <c>Content</c> is null and <c>DeletedAt</c> is set.</summary>
public sealed record MessageResponse(
    long Id,
    Guid ChannelId,
    Guid ClientMessageId,
    MessageAuthor Author,
    string? Content,
    long? ReplyToMessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    DateTimeOffset? DeletedAt);

/// <summary>Messages in ascending ID order. <c>HasMore</c> means another page exists in the requested direction.</summary>
public sealed record MessagePageResponse(IReadOnlyList<MessageResponse> Messages, bool HasMore);

internal static class MessageProblems
{
    public static ProblemHttpResult ChannelNotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "channels.not_found", "Channel not found.");

    /// <summary>Also returned when the caller cannot read the message's channel.</summary>
    public static ProblemHttpResult NotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "messaging.not_found", "Message not found.");

    public static ProblemHttpResult PermissionDenied() =>
        ApiProblems.Create(StatusCodes.Status403Forbidden, "messaging.permission_denied", "You cannot do this with messages in this channel.");

    public static ProblemHttpResult ClientIdConflict() =>
        ApiProblems.Create(StatusCodes.Status409Conflict, "messaging.client_id_conflict", "This client message ID was already used in another channel.");

    public static ProblemHttpResult EditConflict() =>
        ApiProblems.Create(StatusCodes.Status409Conflict, "messaging.edit_conflict", "The message changed while you were editing it. Reload and try again.");
}

/// <summary>Realtime event names (contracts/events/messaging.md). Payload: <see cref="MessageResponse"/>.</summary>
public static class MessagingEvents
{
    public const string MessageCreated = "MessageCreated";
    public const string MessageEdited = "MessageEdited";
    public const string MessageDeleted = "MessageDeleted";
}

internal static class MessageEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static async Task<Results<Created<MessageResponse>, Ok<MessageResponse>, ValidationProblem, ProblemHttpResult>> SendAsync(
        Guid channelId,
        SendMessageRequest request,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        ConversationAccess access,
        IIdentityModule identity,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        if (!await access.CanReadAsync(channelId, userId, cancellationToken))
        {
            return MessageProblems.ChannelNotFound();
        }

        if (!await access.CanWriteAsync(channelId, userId, cancellationToken))
        {
            return MessageProblems.PermissionDenied();
        }

        // A retried send returns the message stored by the first attempt.
        var existing = await FindByClientIdAsync(db, userId, request.ClientMessageId, cancellationToken);
        if (existing is not null)
        {
            return existing.ChannelId == channelId
                ? TypedResults.Ok(await ToResponseAsync(existing, identity, cancellationToken))
                : MessageProblems.ClientIdConflict();
        }

        var errors = new Dictionary<string, string[]>();
        var content = NormalizeContent(request.Content, errors);
        if (request.ClientMessageId == Guid.Empty)
        {
            errors["clientMessageId"] = ["A client message ID is required."];
        }

        if (request.ReplyToMessageId is { } replyTo
            && !await db.Messages.AnyAsync(m => m.Id == replyTo && m.ChannelId == channelId, cancellationToken))
        {
            errors["replyToMessageId"] = ["Replies must point to a message in the same channel."];
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        var message = new Message
        {
            ChannelId = channelId,
            AuthorId = userId,
            ClientMessageId = request.ClientMessageId,
            Content = content,
            ReplyToMessageId = request.ReplyToMessageId,
            CreatedAt = timeProvider.GetDatabaseUtcNow(),
        };
        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(MessagingDbContext.UniqueClientMessageIndex))
        {
            // A concurrent retry with the same client ID won the race.
            db.ChangeTracker.Clear();
            var winner = await FindByClientIdAsync(db, userId, request.ClientMessageId, cancellationToken);
            return winner is not null && winner.ChannelId == channelId
                ? TypedResults.Ok(await ToResponseAsync(winner, identity, cancellationToken))
                : MessageProblems.ClientIdConflict();
        }

        // Authors have read what they wrote: their own message never counts as unread.
        await ReadStateEndpoints.AdvanceAsync(db, userId, channelId, message.Id, timeProvider, cancellationToken);

        var created = await ToResponseAsync(message, identity, cancellationToken);
        await access.PublishAsync(channelId, MessagingEvents.MessageCreated, created, cancellationToken);
        return TypedResults.Created($"/messages/{message.Id}", created);
    }

    /// <summary>
    /// One page of a channel's history in ascending order. Without cursors: the newest page.
    /// <c>before</c>: older messages (scrolling up). <c>after</c>: newer messages (catching up after a reconnect).
    /// </summary>
    public static async Task<Results<Ok<MessagePageResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        Guid channelId,
        long? before,
        long? after,
        int? limit,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        ConversationAccess access,
        IIdentityModule identity,
        CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(channelId, principal.GetRequiredUserId(), cancellationToken))
        {
            return MessageProblems.ChannelNotFound();
        }

        var size = limit ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize || (before is not null && after is not null))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["query"] = [$"Use at most one of 'before' and 'after', and a limit of 1-{MaxPageSize}."],
            });
        }

        var query = db.Messages.AsNoTracking().Where(m => m.ChannelId == channelId);
        List<Message> page;
        if (after is { } afterId)
        {
            page = await query.Where(m => m.Id > afterId).OrderBy(m => m.Id).Take(size + 1).ToListAsync(cancellationToken);
        }
        else
        {
            if (before is { } beforeId)
            {
                query = query.Where(m => m.Id < beforeId);
            }

            page = await query.OrderByDescending(m => m.Id).Take(size + 1).ToListAsync(cancellationToken);
            page.Reverse();
        }

        var hasMore = page.Count > size;
        if (hasMore)
        {
            // The extra row only signals another page; drop it from the far end.
            page.RemoveAt(after is not null ? page.Count - 1 : 0);
        }

        return TypedResults.Ok(new MessagePageResponse(await ToResponsesAsync(page, identity, cancellationToken), hasMore));
    }

    public static async Task<Results<Ok<MessageResponse>, ValidationProblem, ProblemHttpResult>> EditAsync(
        long messageId,
        EditMessageRequest request,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        ConversationAccess access,
        IIdentityModule identity,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        var message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null || message.DeletedAt is not null
            || !await access.CanReadAsync(message.ChannelId, userId, cancellationToken))
        {
            return MessageProblems.NotFound();
        }

        // Only the author edits, and only while they may still post in the channel.
        if (message.AuthorId != userId || !await access.CanWriteAsync(message.ChannelId, userId, cancellationToken))
        {
            return MessageProblems.PermissionDenied();
        }

        var errors = new Dictionary<string, string[]>();
        var content = NormalizeContent(request.Content, errors);
        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        if (content != message.Content)
        {
            message.Content = content;
            message.EditedAt = timeProvider.GetDatabaseUtcNow();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return MessageProblems.EditConflict();
            }

            var edited = await ToResponseAsync(message, identity, cancellationToken);
            await access.PublishAsync(message.ChannelId, MessagingEvents.MessageEdited, edited, cancellationToken);
            return TypedResults.Ok(edited);
        }

        return TypedResults.Ok(await ToResponseAsync(message, identity, cancellationToken));
    }

    /// <summary>Authors delete their own messages; ManageMessages deletes anyone's. Idempotent.</summary>
    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        long messageId,
        ClaimsPrincipal principal,
        MessagingDbContext db,
        ConversationAccess access,
        IIdentityModule identity,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        var message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null || !await access.CanReadAsync(message.ChannelId, userId, cancellationToken))
        {
            return MessageProblems.NotFound();
        }

        if (message.AuthorId != userId && !await access.CanManageAsync(message.ChannelId, userId, cancellationToken))
        {
            return MessageProblems.PermissionDenied();
        }

        if (message.DeletedAt is null)
        {
            message.DeletedAt = timeProvider.GetDatabaseUtcNow();
            message.Content = string.Empty;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Edited or deleted concurrently; the delete still wins on retry by the client.
                return MessageProblems.EditConflict();
            }

            var tombstone = await ToResponseAsync(message, identity, cancellationToken);
            await access.PublishAsync(message.ChannelId, MessagingEvents.MessageDeleted, tombstone, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static Task<Message?> FindByClientIdAsync(MessagingDbContext db, Guid authorId, Guid clientMessageId, CancellationToken cancellationToken) =>
        db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.AuthorId == authorId && m.ClientMessageId == clientMessageId, cancellationToken);

    /// <summary>Trims surrounding whitespace; rejects empty, oversized or control-character content (newlines and tabs are fine).</summary>
    private static string NormalizeContent(string? content, Dictionary<string, string[]> errors)
    {
        var normalized = (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length is 0 or > Message.ContentMaxLength
            || normalized.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
        {
            errors["content"] = [$"Messages must be 1-{Message.ContentMaxLength} characters."];
        }

        return normalized;
    }

    private static async Task<MessageResponse> ToResponseAsync(Message message, IIdentityModule identity, CancellationToken cancellationToken) =>
        (await ToResponsesAsync([message], identity, cancellationToken))[0];

    private static async Task<List<MessageResponse>> ToResponsesAsync(List<Message> messages, IIdentityModule identity, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, PublicUser> authors = messages.Count == 0
            ? new Dictionary<Guid, PublicUser>()
            : await identity.GetPublicUsersAsync(messages.Select(m => m.AuthorId).ToHashSet(), cancellationToken);
        return messages.Select(m => new MessageResponse(
            m.Id,
            m.ChannelId,
            m.ClientMessageId,
            authors.TryGetValue(m.AuthorId, out var author)
                ? new MessageAuthor(m.AuthorId, author.DisplayName, author.AvatarAssetId)
                : new MessageAuthor(m.AuthorId, null, null),
            m.DeletedAt is null ? m.Content : null,
            m.ReplyToMessageId,
            m.CreatedAt,
            m.EditedAt,
            m.DeletedAt)).ToList();
    }
}
