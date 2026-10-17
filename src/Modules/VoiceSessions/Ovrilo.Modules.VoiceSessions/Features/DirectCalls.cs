using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Messaging.Contracts;
using Ovrilo.Modules.VoiceSessions.LiveKit;

namespace Ovrilo.Modules.VoiceSessions.Features;

public sealed record CallUserResponse(Guid Id, string DisplayName, Guid? AvatarAssetId);

/// <summary>Someone is calling you in a direct conversation.</summary>
public sealed record CallRingingEvent(Guid ConversationId, CallUserResponse Caller);

/// <summary>The person you called answered.</summary>
public sealed record CallAcceptedEvent(Guid ConversationId);

public enum CallEndReason
{
    /// <summary>The person called said no.</summary>
    Declined,

    /// <summary>The caller hung up before an answer.</summary>
    Cancelled,

    /// <summary>Nobody answered in time.</summary>
    Missed,
}

public sealed record CallEndedEvent(Guid ConversationId, CallEndReason Reason);

/// <summary>A direct conversation's call: who is ringing whom, and who is connected.</summary>
public sealed record CallStateResponse(Guid ConversationId, Guid? RingingFrom, IReadOnlyList<Guid> ParticipantIds);

/// <summary>Realtime event names, sent to the participants' own connections (contracts/events/README.md).</summary>
public static class CallEvents
{
    public const string CallRinging = nameof(CallRinging);
    public const string CallAccepted = nameof(CallAccepted);
    public const string CallEnded = nameof(CallEnded);
}

/// <summary>
/// Calls that are ringing, per conversation. In memory, like presence: correct for the single API
/// instance deployed today; a restart forgets rings (the caller's client then times out by itself).
/// </summary>
internal sealed class CallTracker(
    IRealtimePublisher realtime,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<CallTracker> logger)
{
    private readonly Dictionary<Guid, Ring> rings = [];
    private readonly Lock gate = new();

    private TimeSpan RingFor => TimeSpan.FromSeconds(configuration.GetValue("VoiceSessions:Calls:RingSeconds", 30.0));

    public (Guid Caller, Guid Callee)? Ringing(Guid conversationId)
    {
        lock (gate)
        {
            return rings.TryGetValue(conversationId, out var ring) ? (ring.Caller, ring.Callee) : null;
        }
    }

    /// <summary>Starts ringing (replacing an older ring); after the ring time it ends as missed.</summary>
    public void Start(Guid conversationId, Guid caller, Guid callee)
    {
        var ring = new Ring(caller, callee, new CancellationTokenSource());
        lock (gate)
        {
            if (rings.Remove(conversationId, out var old))
            {
                old.Timeout.Cancel();
            }

            rings[conversationId] = ring;
        }

        _ = MissAfterAsync(conversationId, ring);
    }

    /// <summary>Stops ringing; returns the ring that was going on, if any.</summary>
    public (Guid Caller, Guid Callee)? Stop(Guid conversationId)
    {
        lock (gate)
        {
            if (!rings.Remove(conversationId, out var ring))
            {
                return null;
            }

            ring.Timeout.Cancel();
            return (ring.Caller, ring.Callee);
        }
    }

    private async Task MissAfterAsync(Guid conversationId, Ring ring)
    {
        try
        {
            await Task.Delay(RingFor, time, ring.Timeout.Token);
            lock (gate)
            {
                if (!rings.TryGetValue(conversationId, out var current) || current != ring)
                {
                    return;
                }

                rings.Remove(conversationId);
            }

            var ended = new CallEndedEvent(conversationId, CallEndReason.Missed);
            await realtime.PublishToUserAsync(ring.Caller, CallEvents.CallEnded, ended);
            await realtime.PublishToUserAsync(ring.Callee, CallEvents.CallEnded, ended);
        }
        catch (OperationCanceledException)
        {
            // Answered, declined or replaced.
        }
        catch (Exception ex)
        {
            CallLog.MissedNotSent(logger, conversationId, ex);
        }
        finally
        {
            ring.Timeout.Dispose();
        }
    }

    private sealed record Ring(Guid Caller, Guid Callee, CancellationTokenSource Timeout);
}

internal static partial class CallLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Missed call in conversation {ConversationId} was not announced")]
    public static partial void MissedNotSent(ILogger logger, Guid conversationId, Exception exception);
}

/// <summary>One-to-one calls in direct conversations: the room is <c>dm_{conversationId}</c>.</summary>
internal static class DirectCalls
{
    /// <summary>Starts a call and rings the other person. If they are already calling you, this answers them.</summary>
    public static async Task<Results<Ok<VoiceJoinResponse>, ProblemHttpResult>> StartAsync(
        Guid conversationId,
        ClaimsPrincipal principal,
        IDirectConversations conversations,
        IIdentityModule identity,
        LiveKitTokenIssuer issuer,
        CallTracker calls,
        IRealtimePublisher realtime,
        IOptions<ScreenSharingOptions> screenSharing,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var (problem, user, other) = await AuthorizeAsync(conversationId, me, conversations, identity, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (calls.Ringing(conversationId) is { } ringing && ringing.Caller == other)
        {
            calls.Stop(conversationId);
            await realtime.PublishToUserAsync(other, CallEvents.CallAccepted, new CallAcceptedEvent(conversationId), cancellationToken);
        }
        else
        {
            calls.Start(conversationId, me, other);
            var caller = new CallUserResponse(user!.Id, user.DisplayName, user.AvatarAssetId);
            await realtime.PublishToUserAsync(other, CallEvents.CallRinging, new CallRingingEvent(conversationId, caller), cancellationToken);
        }

        return TypedResults.Ok(Grant(conversationId, user!, issuer, screenSharing));
    }

    /// <summary>Answers a ringing call, or joins (back) into one that is going on.</summary>
    public static async Task<Results<Ok<VoiceJoinResponse>, ProblemHttpResult>> JoinAsync(
        Guid conversationId,
        ClaimsPrincipal principal,
        IDirectConversations conversations,
        IIdentityModule identity,
        LiveKitTokenIssuer issuer,
        CallTracker calls,
        IRealtimePublisher realtime,
        IOptions<ScreenSharingOptions> screenSharing,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var (problem, user, _) = await AuthorizeAsync(conversationId, me, conversations, identity, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (calls.Ringing(conversationId) is { } ringing && ringing.Callee == me)
        {
            calls.Stop(conversationId);
            await realtime.PublishToUserAsync(ringing.Caller, CallEvents.CallAccepted, new CallAcceptedEvent(conversationId), cancellationToken);
        }

        return TypedResults.Ok(Grant(conversationId, user!, issuer, screenSharing));
    }

    /// <summary>Stops a ringing call: the person called declines, or the caller hangs up first. Idempotent.</summary>
    public static async Task<Results<NoContent, ProblemHttpResult>> DeclineAsync(
        Guid conversationId,
        ClaimsPrincipal principal,
        IDirectConversations conversations,
        CallTracker calls,
        IRealtimePublisher realtime,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var conversation = await conversations.FindAsync(conversationId, cancellationToken);
        if (conversation is null || !conversation.Includes(me))
        {
            return NotFound();
        }

        if (calls.Ringing(conversationId) is not { } ringing || calls.Stop(conversationId) is null)
        {
            return TypedResults.NoContent();
        }

        var ended = new CallEndedEvent(conversationId, me == ringing.Callee ? CallEndReason.Declined : CallEndReason.Cancelled);
        await realtime.PublishToUserAsync(ringing.Caller, CallEvents.CallEnded, ended, cancellationToken);
        await realtime.PublishToUserAsync(ringing.Callee, CallEvents.CallEnded, ended, cancellationToken);
        return TypedResults.NoContent();
    }

    public static async Task<Results<Ok<CallStateResponse>, ProblemHttpResult>> GetAsync(
        Guid conversationId,
        ClaimsPrincipal principal,
        IDirectConversations conversations,
        CallTracker calls,
        IVoiceRoomDirectory rooms,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var conversation = await conversations.FindAsync(conversationId, cancellationToken);
        if (conversation is null || !conversation.Includes(me))
        {
            return NotFound();
        }

        var room = LiveKitTokenIssuer.CallRoomName(conversationId);
        IReadOnlyList<Guid> participants;
        try
        {
            var occupied = await rooms.GetParticipantsAsync([room], cancellationToken);
            participants = occupied.TryGetValue(room, out var list)
                ? [.. list.Select(p => Guid.TryParse(p.Identity, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)]
                : [];
        }
        catch (VoiceRoomDirectoryUnavailableException)
        {
            return ApiProblems.Create(StatusCodes.Status503ServiceUnavailable, "voice.directory_unavailable", "Who is in the call is unknown right now.");
        }

        return TypedResults.Ok(new CallStateResponse(conversationId, calls.Ringing(conversationId)?.Caller, participants));
    }

    /// <summary>The webview's watch-only grant for screen shares in the call (ADR-0017).</summary>
    public static async Task<Results<Ok<VoiceWatchResponse>, ProblemHttpResult>> WatchAsync(
        Guid conversationId,
        ClaimsPrincipal principal,
        IDirectConversations conversations,
        IIdentityModule identity,
        LiveKitTokenIssuer issuer,
        IOptions<ScreenSharingOptions> screenSharing,
        CancellationToken cancellationToken)
    {
        var me = principal.GetRequiredUserId();
        var (problem, user, _) = await AuthorizeAsync(conversationId, me, conversations, identity, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (!screenSharing.Value.Enabled)
        {
            return ApiProblems.Create(StatusCodes.Status403Forbidden, "voice.screen_share_disabled", "Screen sharing is not enabled on this server.");
        }

        var grant = issuer.IssueViewerGrant(LiveKitTokenIssuer.CallRoomName(conversationId), me, user!.DisplayName);
        return TypedResults.Ok(new VoiceWatchResponse(grant.ServerUrl, grant.Token, grant.ExpiresAt));
    }

    /// <summary>Participants may call while they are friends without a block.</summary>
    private static async Task<(ProblemHttpResult? Problem, PublicUser? User, Guid Other)> AuthorizeAsync(
        Guid conversationId,
        Guid me,
        IDirectConversations conversations,
        IIdentityModule identity,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations.FindAsync(conversationId, cancellationToken);
        if (conversation is null || !conversation.Includes(me))
        {
            return (NotFound(), null, Guid.Empty);
        }

        var other = conversation.Other(me);
        var user = await identity.GetPublicUserAsync(me, cancellationToken);
        if (user is null || !await conversations.CanTalkAsync(me, other, cancellationToken))
        {
            return (ApiProblems.Create(StatusCodes.Status403Forbidden, "voice.call_not_allowed", "You can only call your friends."), null, other);
        }

        return (null, user, other);
    }

    private static VoiceJoinResponse Grant(Guid conversationId, PublicUser user, LiveKitTokenIssuer issuer, IOptions<ScreenSharingOptions> screenSharing)
    {
        var canShare = screenSharing.Value.Enabled;
        var grant = issuer.IssueVoiceGrant(LiveKitTokenIssuer.CallRoomName(conversationId), user.Id, user.DisplayName, canShare);
        return new VoiceJoinResponse(grant.ServerUrl, grant.Token, grant.ExpiresAt, canShare);
    }

    private static ProblemHttpResult NotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "voice.call_not_found", "Conversation not found.");
}
