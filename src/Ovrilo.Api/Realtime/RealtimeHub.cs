using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Social.Contracts;

namespace Ovrilo.Api.Realtime;

/// <summary>
/// The single realtime connection per client (ADR-0007): clients subscribe to the channels they
/// view and receive events published by modules, and to the presence of their communities' members.
/// Subscriptions are authorized on every call; the connection itself carries no data the caller
/// could not read over REST.
/// </summary>
[Authorize]
public sealed class RealtimeHub(
    IChannelsModule channels,
    ICommunitiesModule communities,
    ISocialModule social,
    PresenceTracker presence) : Hub
{
    public const string Path = "/hubs/realtime";

    public override Task OnConnectedAsync() => presence.ConnectedAsync(Context.User!.GetRequiredUserId(), Context.ConnectionId);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Disconnected(Context.User!.GetRequiredUserId(), Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// This device's computer is idle (no keyboard or mouse input for a while) or back. The user
    /// shows as away while every connected device is idle.
    /// </summary>
    public Task SetIdle(bool idle) => presence.SetIdleAsync(Context.User!.GetRequiredUserId(), Context.ConnectionId, idle);

    /// <summary>
    /// Starts receiving PresenceChanged for the community's members. Returns the members online now,
    /// or null (and subscribes nothing) when the caller is not an active member.
    /// </summary>
    public async Task<CommunityPresenceResponse?> SubscribeCommunity(Guid communityId)
    {
        var userId = Context.User!.GetRequiredUserId();
        if (await communities.GetPermissionsAsync(communityId, userId, Context.ConnectionAborted) == CommunityPermissions.None)
        {
            return null;
        }

        // Join first: a change after the snapshot is then delivered as an event, never lost in between.
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Community(communityId), Context.ConnectionAborted);
        var members = await communities.ListMemberIdsAsync(communityId, Context.ConnectionAborted);
        var present = Present(members);
        return new CommunityPresenceResponse([.. present.Select(p => p.UserId)], present);
    }

    /// <summary>
    /// Friends online now. Their later changes arrive as PresenceChanged without subscribing; call
    /// again after reconnecting and after FriendsChanged (new friends).
    /// </summary>
    public async Task<FriendsPresenceResponse> GetFriendsPresence()
    {
        var friends = await social.ListFriendIdsAsync(Context.User!.GetRequiredUserId(), Context.ConnectionAborted);
        var present = Present(friends);
        return new FriendsPresenceResponse([.. present.Select(p => p.UserId)], present);
    }

    private List<UserPresence> Present(IEnumerable<Guid> userIds) =>
        [.. userIds.Select(id => (Id: id, Status: presence.StatusOf(id))).Where(p => p.Status is not null).Select(p => new UserPresence(p.Id, p.Status!.Value))];

    public Task UnsubscribeCommunity(Guid communityId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Community(communityId), Context.ConnectionAborted);

    /// <summary>Starts receiving a channel's events. Returns false (and subscribes nothing) when the caller cannot read it.</summary>
    public async Task<bool> SubscribeChannel(Guid channelId)
    {
        var userId = Context.User!.GetRequiredUserId();
        if (!await channels.CanReadChannelAsync(channelId, userId, Context.ConnectionAborted))
        {
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Channel(channelId), Context.ConnectionAborted);
        return true;
    }

    public Task UnsubscribeChannel(Guid channelId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Channel(channelId), Context.ConnectionAborted);
}

/// <summary>Lets the hub address all connections of a user by their user ID.</summary>
internal sealed class RealtimeUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst(IdentityClaims.UserId)?.Value;
}

/// <summary>Who is online now (<c>OnlineUserIds</c>) and how (<c>Statuses</c>, the same people).</summary>
public sealed record CommunityPresenceResponse(IReadOnlyList<Guid> OnlineUserIds, IReadOnlyList<UserPresence>? Statuses = null);

/// <summary>Friends online now (<c>OnlineUserIds</c>) and how (<c>Statuses</c>, the same people).</summary>
public sealed record FriendsPresenceResponse(IReadOnlyList<Guid> OnlineUserIds, IReadOnlyList<UserPresence>? Statuses = null);

internal sealed class SignalRRealtimePublisher(IHubContext<RealtimeHub> hub, ILogger<SignalRRealtimePublisher> logger) : IRealtimePublisher
{
    public Task PublishToChannelAsync(Guid channelId, string eventName, object payload, CancellationToken cancellationToken = default) =>
        SendAsync(hub.Clients.Group(RealtimeGroups.Channel(channelId)), eventName, channelId, payload, cancellationToken);

    public Task PublishToCommunityAsync(Guid communityId, string eventName, object payload, CancellationToken cancellationToken = default) =>
        SendAsync(hub.Clients.Group(RealtimeGroups.Community(communityId)), eventName, communityId, payload, cancellationToken);

    // Clients.User resolves through RealtimeUserIdProvider: the user ID claim.
    public Task PublishToUserAsync(Guid userId, string eventName, object payload, CancellationToken cancellationToken = default) =>
        SendAsync(hub.Clients.User(userId.ToString()), eventName, userId, payload, cancellationToken);

    private async Task SendAsync(IClientProxy clients, string eventName, Guid target, object payload, CancellationToken cancellationToken)
    {
        try
        {
            await clients.SendAsync(eventName, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The write already committed; clients catch up over REST. Never fail the request here.
            Log.PublishFailed(logger, eventName, target, ex);
        }
    }
}

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime event {EventName} for {Target} was not delivered")]
    public static partial void PublishFailed(ILogger logger, string eventName, Guid target, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Presence change for user {UserId} was not delivered")]
    public static partial void PresenceFailed(ILogger logger, Guid userId, Exception exception);
}
