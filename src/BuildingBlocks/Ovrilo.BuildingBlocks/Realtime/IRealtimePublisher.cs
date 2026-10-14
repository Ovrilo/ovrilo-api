namespace Ovrilo.BuildingBlocks.Realtime;

/// <summary>
/// Pushes realtime events to subscribed clients (ADR-0007). Modules publish only after their
/// database commit; delivery is best effort and clients reconcile over REST after reconnecting.
/// Event names and payloads are documented in contracts/events/.
/// </summary>
public interface IRealtimePublisher
{
    /// <summary>Sends <paramref name="payload"/> as <paramref name="eventName"/> to everyone subscribed to the channel.</summary>
    Task PublishToChannelAsync(Guid channelId, string eventName, object payload, CancellationToken cancellationToken = default);

    /// <summary>Sends <paramref name="payload"/> to every open connection of one user (their other devices).</summary>
    Task PublishToUserAsync(Guid userId, string eventName, object payload, CancellationToken cancellationToken = default);
}

/// <summary>Group names shared by the hub (subscriptions) and publishers.</summary>
public static class RealtimeGroups
{
    public static string Channel(Guid channelId) => $"channel:{channelId:N}";

    public static string Community(Guid communityId) => $"community:{communityId:N}";
}
