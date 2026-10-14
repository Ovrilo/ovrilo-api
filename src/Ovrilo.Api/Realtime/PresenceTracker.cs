using Microsoft.AspNetCore.SignalR;

using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Communities.Contracts;

namespace Ovrilo.Api.Realtime;

/// <summary>Sent to the user's communities when they come online or go offline.</summary>
public sealed record PresenceChangedEvent(Guid UserId, bool Online);

/// <summary>
/// Online status from open realtime connections: a user is online while at least one connection is
/// open. Going offline waits a grace period so reconnects (network blips, token refresh closes) do
/// not flicker. State is per API instance: a scale-out needs a shared store (see contracts/events).
/// </summary>
public sealed class PresenceTracker(
    IHubContext<RealtimeHub> hub,
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<PresenceTracker> logger)
{
    public const string PresenceChanged = nameof(PresenceChanged);

    private readonly Dictionary<Guid, Entry> users = [];
    private readonly Lock gate = new();

    private TimeSpan Grace => TimeSpan.FromSeconds(configuration.GetValue("Realtime:PresenceGraceSeconds", 10.0));

    /// <summary>Whether others currently see the user as online.</summary>
    public bool IsOnline(Guid userId)
    {
        lock (gate)
        {
            return users.TryGetValue(userId, out var entry) && entry.Announced;
        }
    }

    public Task ConnectedAsync(Guid userId)
    {
        lock (gate)
        {
            var entry = users.TryGetValue(userId, out var existing) ? existing : users[userId] = new Entry();
            entry.Connections++;
            if (entry.Announced)
            {
                return Task.CompletedTask;
            }

            entry.Announced = true;
        }

        return PublishAsync(userId, online: true);
    }

    public void Disconnected(Guid userId)
    {
        lock (gate)
        {
            if (!users.TryGetValue(userId, out var entry) || --entry.Connections > 0)
            {
                return;
            }
        }

        // Detached on purpose: the disconnecting connection must not wait for the grace period.
        _ = GoOfflineAfterGraceAsync(userId);
    }

    private async Task GoOfflineAfterGraceAsync(Guid userId)
    {
        try
        {
            await Task.Delay(Grace, time);
            lock (gate)
            {
                if (!users.TryGetValue(userId, out var entry) || entry.Connections > 0)
                {
                    return;
                }

                users.Remove(userId);
                if (!entry.Announced)
                {
                    return;
                }
            }

            await PublishAsync(userId, online: false);
        }
        catch (Exception ex)
        {
            Log.PresenceFailed(logger, userId, ex);
        }
    }

    private async Task PublishAsync(Guid userId, bool online)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var communities = await scope.ServiceProvider.GetRequiredService<ICommunitiesModule>().ListCommunityIdsAsync(userId);
            if (communities.Count > 0)
            {
                await hub.Clients.Groups([.. communities.Select(RealtimeGroups.Community)])
                    .SendAsync(PresenceChanged, new PresenceChangedEvent(userId, online));
            }
        }
        catch (Exception ex)
        {
            // Best effort like other events: clients take a fresh snapshot when they (re)subscribe.
            Log.PresenceFailed(logger, userId, ex);
        }
    }

    private sealed class Entry
    {
        public int Connections { get; set; }

        /// <summary>Online as last published to others.</summary>
        public bool Announced { get; set; }
    }
}
