using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Identity.Contracts;

namespace Ovrilo.Api.Realtime;

/// <summary>
/// The single realtime connection per client (ADR-0007): clients subscribe to the channels they
/// view and receive events published by modules, and to the presence of their communities' members.
/// Subscriptions are authorized on every call; the connection itself carries no data the caller
/// could not read over REST.
/// </summary>
[Authorize]
public sealed class RealtimeHub(IChannelsModule channels, ICommunitiesModule communities, PresenceTracker presence) : Hub
{
    public const string Path = "/hubs/realtime";

    public override Task OnConnectedAsync() => presence.ConnectedAsync(Context.User!.GetRequiredUserId());

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Disconnected(Context.User!.GetRequiredUserId());
        return Task.CompletedTask;
    }

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
        return new CommunityPresenceResponse([.. members.Where(presence.IsOnline)]);
    }

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

public sealed record CommunityPresenceResponse(IReadOnlyList<Guid> OnlineUserIds);

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
