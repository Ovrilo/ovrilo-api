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

/// <summary>
/// Someone is calling you in a direct or group conversation. <c>GroupName</c> is the group's
/// name when it has one.
/// </summary>
public sealed record CallRingingEvent(Guid ConversationId, CallUserResponse Caller, bool IsGroup = false, string? GroupName = null);

/// <summary>Someone answered: sent to the caller (first answer only) and to the answering person's own connections.</summary>
public sealed record CallAcceptedEvent(Guid ConversationId);

public enum CallEndReason
{
    /// <summary>The person called said no (in a group: everyone called said no).</summary>
    Declined,

    /// <summary>The caller hung up before an answer.</summary>
    Cancelled,

    /// <summary>Nobody answered in time (sent to the caller only when nobody answered at all).</summary>
    Missed,
}

public sealed record CallEndedEvent(Guid ConversationId, CallEndReason Reason);

/// <summary>
/// A conversation's call: who is ringing (<c>RingingFrom</c>), who is still being rung
/// (<c>RingingIds</c>), and who is connected.
/// </summary>
public sealed record CallStateResponse(Guid ConversationId, Guid? RingingFrom, IReadOnlyList<Guid> ParticipantIds, IReadOnlyList<Guid>? RingingIds = null);

/// <summary>Realtime event names, sent to the participants' own connections (contracts/events/README.md).</summary>
public static class CallEvents
{
    public const string CallRinging = nameof(CallRinging);
    public const string CallAccepted = nameof(CallAccepted);
    public const string CallEnded = nameof(CallEnded);
}

/// <summary>A ringing call: who called, who has not answered yet, and whether anyone has.</summary>
internal sealed record RingState(Guid Caller, IReadOnlyList<Guid> Pending, bool Answered);

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

    public RingState? Ringing(Guid conversationId)
    {
        lock (gate)
        {
            return rings.TryGetValue(conversationId, out var ring) ? ring.State() : null;
        }
    }

    /// <summary>Starts ringing the callees (replacing an older ring); after the ring time it ends as missed.</summary>
    public void Start(Guid conversationId, Guid caller, IEnumerable<Guid> callees)
    {
        var ring = new Ring(caller, [.. callees], new CancellationTokenSource());
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

    /// <summary>
    /// <paramref name="userId"/> answers or declines. Returns the ring as it was before, or null when
    /// they were not being rung. The ring stops once nobody is left to ring.
    /// </summary>
    public RingState? Respond(Guid conversationId, Guid userId, bool answer)
    {
        lock (gate)
        {
            if (!rings.TryGetValue(conversationId, out var ring) || !ring.Pending.Contains(userId))
            {
                return null;
            }

            var before = ring.State();
            ring.Pending.Remove(userId);
            ring.Answered |= answer;
            if (ring.Pending.Count == 0)
            {
                rings.Remove(conversationId);
                ring.Timeout.Cancel();
            }

            return before;
        }
    }

    /// <summary>Stops the whole ring (the caller hung up); returns it, if one was going on.</summary>
    public RingState? Stop(Guid conversationId)
    {
        lock (gate)
        {
            if (!rings.Remove(conversationId, out var ring))
            {
                return null;
            }

            ring.Timeout.Cancel();
            return ring.State();
        }
    }

    private async Task MissAfterAsync(Guid conversationId, Ring ring)
    {
        try
        {
            await Task.Delay(RingFor, time, ring.Timeout.Token);
            RingState missed;
            lock (gate)
            {
                if (!rings.TryGetValue(conversationId, out var current) || current != ring)
                {
                    return;
                }

                rings.Remove(conversationId);
                missed = ring.State();
            }

            var ended = new CallEndedEvent(conversationId, CallEndReason.Missed);
            if (!missed.Answered)
            {
                await realtime.PublishToUserAsync(missed.Caller, CallEvents.CallEnded, ended);
            }

            foreach (var callee in missed.Pending)
            {
                await realtime.PublishToUserAsync(callee, CallEvents.CallEnded, ended);
            }
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

    private sealed class Ring(Guid caller, HashSet<Guid> pending, CancellationTokenSource timeout)
    {
        public Guid Caller { get; } = caller;

        public HashSet<Guid> Pending { get; } = pending;

        public bool Answered { get; set; }

        public CancellationTokenSource Timeout { get; } = timeout;

        public RingState State() => new(Caller, [.. Pending], Answered);
    }
}

internal static partial class CallLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Missed call in conversation {ConversationId} was not announced")]
    public static partial void MissedNotSent(ILogger logger, Guid conversationId, Exception exception);
}

/// <summary>Calls in direct and group conversations: the room is <c>dm_{conversationId}</c>.</summary>
internal static class DirectCalls
{
    /// <summary>Starts a call and rings the other members. If you are being rung, this answers instead.</summary>
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
        var (problem, user, conversation) = await AuthorizeAsync(conversationId, me, conversations, identity, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (!await AnswerAsync(conversationId, me, calls, realtime, cancellationToken))
        {
            var callees = conversation!.Others(me).ToList();
            calls.Start(conversationId, me, callees);
            var ringing = new CallRingingEvent(
                conversationId,
                new CallUserResponse(user!.Id, user.DisplayName, user.AvatarAssetId),
                conversation.IsGroup,
                conversation.Name);
            foreach (var callee in callees)
            {
                await realtime.PublishToUserAsync(callee, CallEvents.CallRinging, ringing, cancellationToken);
            }
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

        await AnswerAsync(conversationId, me, calls, realtime, cancellationToken);
        return TypedResults.Ok(Grant(conversationId, user!, issuer, screenSharing));
    }

    /// <summary>
    /// Stops ringing: someone being rung declines (for themselves), or the caller hangs up first
    /// (for everyone). Idempotent.
    /// </summary>
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

        if (calls.Ringing(conversationId)?.Caller == me && calls.Stop(conversationId) is { } cancelled)
        {
            var ended = new CallEndedEvent(conversationId, CallEndReason.Cancelled);
            foreach (var userId in cancelled.Pending.Prepend(me))
            {
                await realtime.PublishToUserAsync(userId, CallEvents.CallEnded, ended, cancellationToken);
            }
        }
        else if (calls.Respond(conversationId, me, answer: false) is { } ring)
        {
            var declined = new CallEndedEvent(conversationId, CallEndReason.Declined);
            await realtime.PublishToUserAsync(me, CallEvents.CallEnded, declined, cancellationToken);
            // The caller hears "declined" once everyone they rang has said no.
            if (!ring.Answered && ring.Pending.Count == 1)
            {
                await realtime.PublishToUserAsync(ring.Caller, CallEvents.CallEnded, declined, cancellationToken);
            }
        }

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

        var ringing = calls.Ringing(conversationId);
        return TypedResults.Ok(new CallStateResponse(conversationId, ringing?.Caller, participants, ringing?.Pending ?? []));
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

    /// <summary>If <paramref name="me"/> is being rung, answers: tells the caller (on the first answer) and my other devices.</summary>
    private static async Task<bool> AnswerAsync(Guid conversationId, Guid me, CallTracker calls, IRealtimePublisher realtime, CancellationToken cancellationToken)
    {
        if (calls.Respond(conversationId, me, answer: true) is not { } ring)
        {
            return false;
        }

        var accepted = new CallAcceptedEvent(conversationId);
        if (!ring.Answered)
        {
            await realtime.PublishToUserAsync(ring.Caller, CallEvents.CallAccepted, accepted, cancellationToken);
        }

        await realtime.PublishToUserAsync(me, CallEvents.CallAccepted, accepted, cancellationToken);
        return true;
    }

    /// <summary>
    /// Direct conversations: the two may call while they are friends without a block. Groups: any
    /// member may call.
    /// </summary>
    private static async Task<(ProblemHttpResult? Problem, PublicUser? User, ConversationInfo? Conversation)> AuthorizeAsync(
        Guid conversationId,
        Guid me,
        IDirectConversations conversations,
        IIdentityModule identity,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations.FindAsync(conversationId, cancellationToken);
        if (conversation is null || !conversation.Includes(me))
        {
            return (NotFound(), null, null);
        }

        var user = await identity.GetPublicUserAsync(me, cancellationToken);
        var allowed = user is not null
            && (conversation.IsGroup || await conversations.CanTalkAsync(me, conversation.Others(me).First(), cancellationToken));
        if (!allowed)
        {
            return (ApiProblems.Create(StatusCodes.Status403Forbidden, "voice.call_not_allowed", "You can only call your friends."), null, conversation);
        }

        return (null, user, conversation);
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
